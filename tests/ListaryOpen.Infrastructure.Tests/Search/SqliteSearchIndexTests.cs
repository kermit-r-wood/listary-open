using System.Diagnostics;
using System.Globalization;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Core.Usage;
using ListaryOpen.Infrastructure.Indexing.Ntfs;
using ListaryOpen.Infrastructure.Search;
using Microsoft.Data.Sqlite;

namespace ListaryOpen.Infrastructure.Tests.Search;

public sealed class SqliteSearchIndexTests
{
    [Fact]
    public async Task OpenEnablesWalForConcurrentSearchAndIndexing()
    {
        var dbPath = CreateTempDbPath();
        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            await using (var connection = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "pragma journal_mode;";

                var mode = Convert.ToString(await command.ExecuteScalarAsync());

                Assert.Equal("wal", mode, ignoreCase: true);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    private const int EarlierRowCount = 5001;

    [Fact]
    public async Task SearchReturnsInsertedRecord()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await index.UpsertAsync(FileRecord.Create("C:\\Docs\\Invoice.xlsx", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);

                var results = await index.SearchAsync(new SearchQuery("invoice", SearchMode.FilesAndFolders), CancellationToken.None);

                var result = Assert.Single(results);
                Assert.Equal("Invoice.xlsx", result.Record.Name);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task DeleteRemovesRecord()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await index.UpsertAsync(FileRecord.Create("C:\\Docs\\Invoice.xlsx", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);
                await index.DeleteAsync("C:\\Docs\\Invoice.xlsx", CancellationToken.None);

                var results = await index.SearchAsync(new SearchQuery("invoice", SearchMode.FilesAndFolders), CancellationToken.None);

                Assert.Empty(results);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchFindsChineseFilenameByPinyinQuery()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await index.UpsertAsync(FileRecord.Create("C:\\Docs\\合同.docx", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);

                var results = await index.SearchAsync(new SearchQuery("hetong", SearchMode.FilesAndFolders), CancellationToken.None);

                var result = Assert.Single(results);
                Assert.Equal("合同.docx", result.Record.Name);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task DeleteUsesCaseInsensitivePathKeyIdentity()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await index.UpsertAsync(FileRecord.Create("C:\\Docs\\Invoice.xlsx", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);
                await index.DeleteAsync("c:\\docs\\invoice.xlsx", CancellationToken.None);

                var results = await index.SearchAsync(new SearchQuery("invoice", SearchMode.FilesAndFolders), CancellationToken.None);

                Assert.Empty(results);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task DeleteRejectsInvalidPath()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                var exception = await Assert.ThrowsAsync<ArgumentException>(
                    () => index.DeleteAsync("Invoice.xlsx", CancellationToken.None));

                Assert.Contains("fully qualified", exception.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchReturnsRawMatchOutsideFirstFiveThousandNames()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await InsertAlphabeticallyEarlierRowsAsync(index);
                await index.UpsertAsync(FileRecord.Create("C:\\Docs\\ZTargetInvoice.xlsx", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);

                var results = await index.SearchAsync(new SearchQuery("invoice", SearchMode.FilesAndFolders), CancellationToken.None);

                var result = Assert.Single(results);
                Assert.Equal("ZTargetInvoice.xlsx", result.Record.Name);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchReturnsPinyinMatchOutsideFirstFiveThousandNames()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await InsertAlphabeticallyEarlierRowsAsync(index);
                await index.UpsertAsync(FileRecord.Create("C:\\Docs\\合同.docx", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);

                var results = await index.SearchAsync(new SearchQuery("hetong", SearchMode.FilesAndFolders), CancellationToken.None);

                var result = Assert.Single(results);
                Assert.Equal("合同.docx", result.Record.Name);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchAppliesExtensionPathPhraseAndExclusionFilters()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await index.UpsertManyAsync(new[]
                {
                    FileRecord.Create("C:\\src\\Search Panel.pdf", false, 10, DateTimeOffset.UtcNow),
                    FileRecord.Create("C:\\src\\Search Panel archive.pdf", false, 10, DateTimeOffset.UtcNow),
                    FileRecord.Create("C:\\docs\\Search Panel.pdf", false, 10, DateTimeOffset.UtcNow),
                    FileRecord.Create("C:\\src\\Search Panel.txt", false, 10, DateTimeOffset.UtcNow)
                }, CancellationToken.None);

                var results = await index.SearchAsync(
                    new SearchQuery("ext:pdf path:src \"Search Panel\" !archive", SearchMode.FilesAndFolders),
                    CancellationToken.None);

                var result = Assert.Single(results);
                Assert.Equal("C:\\src\\Search Panel.pdf", result.Record.FullPath);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchAppliesExtensionFilterBeforeCandidateLimit()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await InsertMatchingInvoiceFilesAsync(index, 1_100);
                await index.UpsertAsync(FileRecord.Create("C:\\Docs\\ZTargetInvoice.pdf", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);

                var results = await index.SearchAsync(new SearchQuery("ext:pdf invoice", SearchMode.FilesAndFolders), CancellationToken.None);

                var result = Assert.Single(results);
                Assert.Equal("ZTargetInvoice.pdf", result.Record.Name);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchSupportsPureExtensionExclusion()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await index.UpsertManyAsync(new[]
                {
                    FileRecord.Create("C:\\Docs\\Keep.txt", false, 10, DateTimeOffset.UtcNow),
                    FileRecord.Create("C:\\Docs\\Drop.tmp", false, 10, DateTimeOffset.UtcNow)
                }, CancellationToken.None);

                var results = await index.SearchAsync(new SearchQuery("!ext:tmp", SearchMode.FilesAndFolders), CancellationToken.None);

                var result = Assert.Single(results);
                Assert.Equal("Keep.txt", result.Record.Name);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchUsesFilterReasonForPureExtensionFilter()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await index.UpsertManyAsync(new[]
                {
                    FileRecord.Create("C:\\Docs\\Alpha.pdf", false, 10, DateTimeOffset.UtcNow),
                    FileRecord.Create("C:\\Docs\\Beta.txt", false, 10, DateTimeOffset.UtcNow)
                }, CancellationToken.None);

                var result = Assert.Single(await index.SearchAsync(
                    new SearchQuery("ext:pdf", SearchMode.FilesAndFolders),
                    CancellationToken.None));

                Assert.Equal("Alpha.pdf", result.Record.Name);
                Assert.Equal("filter", result.MatchReason);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchPureExtensionFilterRespectsLimitsAboveFallbackCandidateWindow()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await index.UpsertManyAsync(
                    Enumerable.Range(0, 250)
                        .Select(index => FileRecord.Create(
                            $"C:\\Docs\\Filtered-{index:D3}.pdf",
                            false,
                            10,
                            DateTimeOffset.UtcNow)),
                    CancellationToken.None);

                var results = await index.SearchAsync(
                    new SearchQuery("ext:pdf", SearchMode.FilesAndFolders, limit: 250),
                    CancellationToken.None);

                Assert.Equal(250, results.Count);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchFolderFilterReturnsOnlyFolders()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await index.UpsertManyAsync(new[]
                {
                    FileRecord.Create("C:\\Reports", true, 0, DateTimeOffset.UtcNow),
                    FileRecord.Create("C:\\Reports.txt", false, 10, DateTimeOffset.UtcNow)
                }, CancellationToken.None);

                var results = await index.SearchAsync(new SearchQuery("folder: report", SearchMode.FilesAndFolders), CancellationToken.None);

                var result = Assert.Single(results);
                Assert.True(result.Record.IsDirectory);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchFileFilterReturnsOnlyFiles()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await index.UpsertManyAsync(new[]
                {
                    FileRecord.Create("C:\\Reports", true, 0, DateTimeOffset.UtcNow),
                    FileRecord.Create("C:\\Reports.txt", false, 10, DateTimeOffset.UtcNow)
                }, CancellationToken.None);

                var results = await index.SearchAsync(new SearchQuery("file: report", SearchMode.FoldersOnly), CancellationToken.None);

                var result = Assert.Single(results);
                Assert.False(result.Record.IsDirectory);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchReturnsTrigramSubstringMatchOutsideFirstFiveThousandNames()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await InsertAlphabeticallyEarlierRowsAsync(index);
                await index.UpsertAsync(FileRecord.Create("C:\\Docs\\ZTargetInvoice.xlsx", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);

                var results = await index.SearchAsync(new SearchQuery("targetinvoice", SearchMode.FilesAndFolders), CancellationToken.None);

                var result = Assert.Single(results);
                Assert.Equal("ZTargetInvoice.xlsx", result.Record.Name);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchFoldersOnlyReturnsMatchingFolderAfterManyMatchingFiles()
    {
        var dbPath = CreateTempDbPath();
        const string targetName = "ZInvoiceFolderWithLongName";

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await InsertMatchingInvoiceFilesAsync(index, 250);
                await index.UpsertAsync(FileRecord.Create($"C:\\Docs\\{targetName}", true, 0, DateTimeOffset.UtcNow), CancellationToken.None);

                var results = await index.SearchAsync(new SearchQuery("invoice", SearchMode.FoldersOnly, limit: 10), CancellationToken.None);

                var result = Assert.Single(results);
                Assert.Equal(targetName, result.Record.Name);
                Assert.True(result.Record.IsDirectory);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task ReopenPersistsInsertedRecord()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await index.UpsertAsync(FileRecord.Create("C:\\Docs\\Invoice.xlsx", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);
            }

            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                var results = await index.SearchAsync(new SearchQuery("invoice", SearchMode.FilesAndFolders), CancellationToken.None);

                var result = Assert.Single(results);
                Assert.Equal("Invoice.xlsx", result.Record.Name);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task VolumeCheckpointRoundTripsUnsignedValuesAsText()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                var checkpoint = new UsnJournalCheckpoint(
                    "C:\\",
                    "NTFS",
                    ulong.MaxValue,
                    long.MaxValue,
                    SqliteSearchIndex.CurrentIndexContentVersion,
                    new DateTimeOffset(2026, 7, 9, 1, 2, 3, TimeSpan.Zero));

                await index.SaveVolumeCheckpointAsync(checkpoint, CancellationToken.None);
                var roundTripped = await index.ReadVolumeCheckpointAsync("C:\\", CancellationToken.None);

                Assert.Equal(checkpoint, roundTripped);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task ApplyUsnJournalChangesDeletesUpsertsAndSavesCheckpointAtomically()
    {
        var dbPath = CreateTempDbPath();
        var oldRecord = FileRecord.Create("C:\\Docs\\OldName.txt", false, 10, DateTimeOffset.UtcNow);
        var newRecord = FileRecord.Create("C:\\Docs\\NewName.txt", false, 20, DateTimeOffset.UtcNow);
        var checkpoint = new UsnJournalCheckpoint(
            "C:\\",
            "NTFS",
            99,
            500,
            SqliteSearchIndex.CurrentIndexContentVersion,
            new DateTimeOffset(2026, 7, 9, 3, 0, 0, TimeSpan.Zero));

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await index.UpsertAsync(oldRecord, CancellationToken.None);

                await index.ApplyUsnJournalChangesAsync(
                    [
                        UsnJournalIndexChange.Delete(oldRecord.FullPath),
                        UsnJournalIndexChange.Upsert(newRecord)
                    ],
                    checkpoint,
                    CancellationToken.None);

                var oldResults = await index.SearchAsync(new SearchQuery("OldName", SearchMode.FilesAndFolders), CancellationToken.None);
                var newResults = await index.SearchAsync(new SearchQuery("NewName", SearchMode.FilesAndFolders), CancellationToken.None);
                var savedCheckpoint = await index.ReadVolumeCheckpointAsync("C:\\", CancellationToken.None);

                Assert.Empty(oldResults);
                Assert.Equal("NewName.txt", Assert.Single(newResults).Record.Name);
                Assert.Equal(checkpoint, savedCheckpoint);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task ApplyUsnJournalChangesRollsBackFilesAndCheckpointWhenAChangeFails()
    {
        var dbPath = CreateTempDbPath();
        var newRecord = FileRecord.Create("C:\\Docs\\ShouldRollback.txt", false, 20, DateTimeOffset.UtcNow);
        var checkpoint = new UsnJournalCheckpoint(
            "C:\\",
            "NTFS",
            99,
            500,
            SqliteSearchIndex.CurrentIndexContentVersion,
            new DateTimeOffset(2026, 7, 9, 3, 0, 0, TimeSpan.Zero));

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await Assert.ThrowsAsync<ArgumentException>(() =>
                    index.ApplyUsnJournalChangesAsync(
                        [
                            UsnJournalIndexChange.Upsert(newRecord),
                            UsnJournalIndexChange.Delete("relative.txt")
                        ],
                        checkpoint,
                        CancellationToken.None));

                var results = await index.SearchAsync(new SearchQuery("ShouldRollback", SearchMode.FilesAndFolders), CancellationToken.None);
                var savedCheckpoint = await index.ReadVolumeCheckpointAsync("C:\\", CancellationToken.None);

                Assert.Empty(results);
                Assert.Null(savedCheckpoint);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task UsnJournalChangeApplierRequestsFullRescanForDirectoryRenameWithoutSavingCheckpoint()
    {
        var dbPath = CreateTempDbPath();
        var checkpoint = new UsnJournalCheckpoint(
            "C:\\",
            "NTFS",
            99,
            500,
            SqliteSearchIndex.CurrentIndexContentVersion,
            new DateTimeOffset(2026, 7, 9, 3, 0, 0, TimeSpan.Zero));

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                var result = await UsnJournalChangeApplier.ApplyAsync(
                    index,
                    [UsnJournalChange.DirectoryRenameOrMove()],
                    checkpoint,
                    CancellationToken.None);

                var savedCheckpoint = await index.ReadVolumeCheckpointAsync("C:\\", CancellationToken.None);

                Assert.True(result.RequiresFullRescan);
                Assert.Equal(0, result.AppliedCount);
                Assert.Null(savedCheckpoint);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task OpenClearsFilesWhenIndexContentVersionIsMissingAndPreservesUsage()
    {
        var dbPath = CreateTempDbPath();
        var oldRecord = FileRecord.Create("C:\\Docs\\OldExcludedNodeModule.js", false, 10, DateTimeOffset.UtcNow);

        try
        {
            await CreateCurrentSchemaDatabaseWithoutMetadataAsync(dbPath, oldRecord);
            await InsertUsageRowsAsync(dbPath, new[] { (oldRecord.FullPath, 7, DateTimeOffset.UtcNow) });

            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                var oldResults = await index.SearchAsync(new SearchQuery("OldExcludedNodeModule", SearchMode.FilesAndFolders), CancellationToken.None);

                Assert.Empty(oldResults);
            }

            Assert.Equal(0, await CountRowsAsync(dbPath, "files"));
            Assert.Equal(1, await CountRowsAsync(dbPath, "usage"));
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task ImportUsageFromAsyncCopiesLegacyUsageWithoutImportingLegacyFiles()
    {
        var dbPath = CreateTempDbPath();
        var legacyDbPath = CreateTempDbPath();
        var importedRecord = FileRecord.Create("C:\\Docs\\ImportedUsageBravo.txt", false, 10, DateTimeOffset.UtcNow);
        var legacyOnlyRecord = FileRecord.Create("C:\\Docs\\LegacyOnlyFile.txt", false, 10, DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;

        try
        {
            await CreateCurrentSchemaDatabaseWithoutMetadataAsync(legacyDbPath, legacyOnlyRecord);
            await InsertUsageRowsAsync(legacyDbPath, new[] { (importedRecord.FullPath, 9, now) });

            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await index.ImportUsageFromAsync(legacyDbPath, CancellationToken.None);
                await index.UpsertManyAsync(new[]
                {
                    FileRecord.Create("C:\\Docs\\ImportedUsageBravo.txt", false, 10, now),
                    FileRecord.Create("C:\\Docs\\ImportedUsageAlpha.txt", false, 10, now)
                }, CancellationToken.None);

                var results = await index.SearchAsync(new SearchQuery("ImportedUsage", SearchMode.FilesAndFolders, limit: 2), CancellationToken.None);

                Assert.Equal("ImportedUsageBravo.txt", results[0].Record.Name);
                Assert.Equal("name-prefix", results[0].MatchReason);
            }

            Assert.Equal(1, await CountRowsAsync(dbPath, "usage"));
            Assert.Equal(0, await CountMatchingFilesAsync(dbPath, legacyOnlyRecord.PathKey));
        }
        finally
        {
            DeleteIfExists(dbPath);
            DeleteIfExists(legacyDbPath);
        }
    }

    [Fact]
    public async Task RecordUsageAsyncIncrementsOpenCountAndUpdatesTimestamp()
    {
        var dbPath = CreateTempDbPath();
        var fullPath = "C:\\Docs\\Invoice.xlsx";
        var before = DateTimeOffset.UtcNow;
        DateTimeOffset after;

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await index.RecordUsageAsync(fullPath, CancellationToken.None);
                await index.RecordUsageAsync(fullPath, CancellationToken.None);
                after = DateTimeOffset.UtcNow;
            }

            var usage = await ReadUsageRowAsync(dbPath, FileRecord.Create(
                fullPath,
                isDirectory: false,
                sizeBytes: 0,
                DateTimeOffset.UnixEpoch).PathKey);

            Assert.Equal(2, usage.OpenCount);
            Assert.InRange(usage.LastUsedAt, before, after);
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchRoundTripsLastWriteTimeOffset()
    {
        var dbPath = CreateTempDbPath();
        var lastWriteTime = new DateTimeOffset(2026, 7, 3, 17, 45, 12, TimeSpan.FromHours(8));

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await index.UpsertAsync(FileRecord.Create("C:\\Docs\\Invoice.xlsx", false, 10, lastWriteTime), CancellationToken.None);

                var results = await index.SearchAsync(new SearchQuery("invoice", SearchMode.FilesAndFolders), CancellationToken.None);

                var result = Assert.Single(results);
                Assert.Equal(lastWriteTime, result.Record.LastWriteTime);
                Assert.Equal(lastWriteTime.Offset, result.Record.LastWriteTime.Offset);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SchemaRejectsInvalidRows()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
            }

            await AssertSqliteConstraintAsync(
                dbPath,
                """
                insert into files(full_path, path_key, name, parent_path, search_text, is_directory, size_bytes, last_write_time)
                values (null, 'BAD1', 'Bad.txt', 'C:\Docs', 'bad.txt', 0, 0, '2026-07-03T01:00:00.0000000+00:00');
                """);
            await AssertSqliteConstraintAsync(
                dbPath,
                """
                insert into files(full_path, path_key, name, parent_path, search_text, is_directory, size_bytes, last_write_time)
                values ('C:\Docs\Bad.txt', 'BAD2', 'Bad.txt', 'C:\Docs', 'bad.txt', 2, 0, '2026-07-03T01:00:00.0000000+00:00');
                """);
            await AssertSqliteConstraintAsync(
                dbPath,
                """
                insert into files(full_path, path_key, name, parent_path, search_text, is_directory, size_bytes, last_write_time)
                values ('C:\Docs\Bad.txt', 'BAD3', 'Bad.txt', 'C:\Docs', 'bad.txt', 0, -1, '2026-07-03T01:00:00.0000000+00:00');
                """);
            await AssertSqliteConstraintAsync(
                dbPath,
                """
                insert into usage(full_path, path_key, open_count, last_used_at)
                values ('C:\Docs\Bad.txt', 'BAD4', -1, '2026-07-03T01:00:00.0000000+00:00');
                """);
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task UpsertManyInsertsMultipleRecords()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                var records = new[]
                {
                    FileRecord.Create("C:\\Docs\\Invoice.xlsx", false, 10, DateTimeOffset.UtcNow),
                    FileRecord.Create("C:\\Docs\\Budget.xlsx", false, 10, DateTimeOffset.UtcNow)
                };

                await index.UpsertManyAsync(records, CancellationToken.None);

                var invoiceResults = await index.SearchAsync(new SearchQuery("invoice", SearchMode.FilesAndFolders), CancellationToken.None);
                var budgetResults = await index.SearchAsync(new SearchQuery("budget", SearchMode.FilesAndFolders), CancellationToken.None);

                Assert.Equal("Invoice.xlsx", Assert.Single(invoiceResults).Record.Name);
                Assert.Equal("Budget.xlsx", Assert.Single(budgetResults).Record.Name);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task ConcurrentSearchAndWritesOnSingleIndexComplete()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await index.UpsertAsync(FileRecord.Create("C:\\Docs\\SeedInvoice.xlsx", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);

                var tasks = new List<Task>();
                for (var worker = 0; worker < 8; worker++)
                {
                    var workerId = worker;
                    tasks.Add(Task.Run(async () =>
                    {
                        var records = Enumerable
                            .Range(0, 250)
                            .Select(i => FileRecord.Create($"C:\\Docs\\Concurrent-{workerId:D2}-{i:D4}.txt", false, 1, DateTimeOffset.UtcNow));

                        await index.UpsertManyAsync(records, CancellationToken.None);
                    }));

                    tasks.Add(Task.Run(async () =>
                    {
                        for (var i = 0; i < 80; i++)
                        {
                            await index.SearchAsync(new SearchQuery("invoice", SearchMode.FilesAndFolders), CancellationToken.None);
                        }
                    }));
                }

                var exception = await Record.ExceptionAsync(() => Task.WhenAll(tasks));

                Assert.Null(exception);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchAsyncDoesNotPostContinuationToCallingSynchronizationContextWhileWaitingForGate()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                var enteredGate = new ManualResetEventSlim();
                var releaseGate = new ManualResetEventSlim();
                var holdGateTask = Task.Run(() => index.UpsertManyAsync(
                    CreateRecordsThatHoldEnumeration(enteredGate, releaseGate),
                    CancellationToken.None));

                Assert.True(enteredGate.Wait(TimeSpan.FromSeconds(5)));

                var previousContext = SynchronizationContext.Current;
                var context = new RecordingSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(context);
                Task<IReadOnlyList<SearchResult>> searchTask;
                try
                {
                    searchTask = index.SearchAsync(new SearchQuery("invoice", SearchMode.FilesAndFolders), CancellationToken.None);
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }

                releaseGate.Set();
                await holdGateTask.WaitAsync(TimeSpan.FromSeconds(5));
                await searchTask.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(0, context.PostCount);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task OpenMigratesOldSchemaDatabaseClearsStaleFilesAndStillAllowsUpsert()
    {
        var dbPath = CreateTempDbPath();
        var oldRecord = FileRecord.Create("C:\\Docs\\OldInvoice.xlsx", false, 10, DateTimeOffset.UtcNow);

        try
        {
            await CreateOldSchemaDatabaseAsync(dbPath, oldRecord);

            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                var oldResults = await index.SearchAsync(new SearchQuery("oldinvoice", SearchMode.FilesAndFolders), CancellationToken.None);

                Assert.Empty(oldResults);

                await index.UpsertAsync(FileRecord.Create("C:\\Docs\\NewInvoice.xlsx", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);

                var newResults = await index.SearchAsync(new SearchQuery("newinvoice", SearchMode.FilesAndFolders), CancellationToken.None);

                Assert.Equal("NewInvoice.xlsx", Assert.Single(newResults).Record.Name);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task OpenMigratesExistingDatabaseAndCreatesSearchIndexes()
    {
        var dbPath = CreateTempDbPath();
        var oldRecord = FileRecord.Create("C:\\Docs\\OldInvoice.xlsx", false, 10, DateTimeOffset.UtcNow);

        try
        {
            await CreateOldSchemaDatabaseAsync(dbPath, oldRecord);

            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
            }

            var indexNames = await ReadIndexNamesAsync(dbPath);

            Assert.Contains("ix_files_name", indexNames);
            Assert.Contains("ix_files_is_directory_name", indexNames);
            Assert.Contains("ix_files_search_text", indexNames);
            Assert.Contains("ix_usage_path_key", indexNames);
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchReturnsExactMatchAfterMoreThanFiveThousandEarlierFuzzyCandidates()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await InsertAlphabeticallyEarlierFuzzyInvoiceRowsAsync(index);
                await index.UpsertAsync(FileRecord.Create("C:\\Docs\\Invoice.xlsx", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);

                var results = await index.SearchAsync(new SearchQuery("invoice", SearchMode.FilesAndFolders), CancellationToken.None);

                Assert.Equal("Invoice.xlsx", results[0].Record.Name);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchAppliesUsageBoostWhenUsageTableHasManyUnrelatedRows()
    {
        var dbPath = CreateTempDbPath();
        var now = DateTimeOffset.UtcNow;
        var unusedRecord = FileRecord.Create("C:\\Docs\\Invoice Alpha.xlsx", false, 10, now);
        var usedRecord = FileRecord.Create("C:\\Docs\\Invoice Beta.xlsx", false, 10, now);

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await index.UpsertManyAsync(new[] { unusedRecord, usedRecord }, CancellationToken.None);

                var unrelatedUsageRows = CreateUnrelatedUsageRows(20_000, now.AddDays(-90))
                    .Append((usedRecord.FullPath, 20, now));
                await InsertUsageRowsAsync(dbPath, unrelatedUsageRows);

                var results = await index.SearchAsync(new SearchQuery("invoice", SearchMode.FilesAndFolders, limit: 5), CancellationToken.None);

                Assert.Equal(usedRecord.Name, results[0].Record.Name);
                Assert.Equal("name-prefix", results[0].MatchReason);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchIncludesUsageBoostedMatchOutsideNameOrderedCandidateWindow()
    {
        var dbPath = CreateTempDbPath();
        var now = DateTimeOffset.UtcNow;
        var usedRecord = FileRecord.Create("C:\\Docs\\ZUsedInvoiceRecord.xlsx", false, 10, now);

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await InsertMatchingInvoiceFilesAsync(index, 250);
                await index.UpsertAsync(usedRecord, CancellationToken.None);
                await InsertUsageRowsAsync(dbPath, new[] { (usedRecord.FullPath, 25, now) });

                var results = await index.SearchAsync(new SearchQuery("invoice", SearchMode.FilesAndFolders, limit: 10), CancellationToken.None);

                Assert.Equal("Invoice0000.txt", results[0].Record.Name);
                Assert.Equal("name-prefix", results[0].MatchReason);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchIncludesRecentCappedUsageMatchAfterManyStaleHigherOpenCountMatches()
    {
        var dbPath = CreateTempDbPath();
        var now = DateTimeOffset.UtcNow;
        var staleLastUsedAt = now.AddDays(-90);
        var target = FileRecord.Create("C:\\Docs\\ZRecentInvoiceUsageTarget.xlsx", false, 10, now);

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                var staleRecords = Enumerable
                    .Range(0, 250)
                    .Select(i => FileRecord.Create($"C:\\Docs\\AInvoiceStaleUsage-{i:D4}.txt", false, 1, now))
                    .ToArray();

                await index.UpsertManyAsync(staleRecords, CancellationToken.None);
                await index.UpsertAsync(target, CancellationToken.None);

                var usageRows = staleRecords
                    .Select(record => (record.FullPath, OpenCount: 100, LastUsedAt: staleLastUsedAt))
                    .Append((target.FullPath, OpenCount: 10, LastUsedAt: now));
                await InsertUsageRowsAsync(dbPath, usageRows);

                var results = await index.SearchAsync(new SearchQuery("invoice", SearchMode.FilesAndFolders, limit: 10), CancellationToken.None);

                Assert.Equal("AInvoiceStaleUsage-0000.txt", results[0].Record.Name);
                Assert.Equal("name-substring", results[0].MatchReason);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchIncludesCombinedScoreWinnerOutsideSeparateCandidateWindows()
    {
        var dbPath = CreateTempDbPath();
        var query = new SearchQuery("invoice2026", SearchMode.FilesAndFolders, limit: 10);
        var now = DateTimeOffset.UtcNow;
        var target = FileRecord.Create("C:\\Docs\\Invoice 2026.xlsx", false, 10, now);
        var exactNoUsageRecords = Enumerable
            .Range(0, 250)
            .Select(i => FileRecord.Create($"C:\\Docs\\Ainvoice2026-{i:D4}.txt", false, 1, now))
            .ToArray();
        var recentWeakUsageRecords = Enumerable
            .Range(0, 250)
            .Select(i => FileRecord.Create(
                $"C:\\Docs\\Axxxxixxxxnxxxxvxxxxoxxxxixxxxcxxxxexxxx2xxxx0xxxx2xxxx6-{i:D4}.txt",
                false,
                1,
                now))
            .ToArray();
        var usageRecords = recentWeakUsageRecords
            .Select(record => new UsageRecord(record.FullPath, 10, now))
            .Append(new UsageRecord(target.FullPath, 10, now.AddDays(-1)))
            .ToArray();

        var allRelevantRecords = exactNoUsageRecords
            .Concat(recentWeakUsageRecords)
            .Append(target)
            .ToArray();
        var rankedFromAllRelevantRecords = ResultRanker.Rank(
            query,
            allRelevantRecords,
            usageRecords,
            Array.Empty<string>());

        Assert.Equal("Ainvoice2026-0000.txt", rankedFromAllRelevantRecords[0].Record.Name);

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await index.UpsertManyAsync(allRelevantRecords, CancellationToken.None);
                await InsertUsageRowsAsync(
                    dbPath,
                    usageRecords.Select(record => (record.FullPath, record.OpenCount, record.LastUsedAt)));

                var results = await index.SearchAsync(query, CancellationToken.None);

                Assert.Equal("Ainvoice2026-0000.txt", results[0].Record.Name);
                Assert.Equal("name-substring", results[0].MatchReason);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchLargeIndexReturnsTargetWithinPerformanceBudget()
    {
        var dbPath = CreateTempDbPath();
        const int fillerCount = 50_000;
        const string targetName = "ZInvoice 2026.xlsx";
        var budget = TimeSpan.FromSeconds(30);

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await InsertWeakIv26RowsAsync(index, fillerCount);
                await index.UpsertAsync(FileRecord.Create($"C:\\Docs\\{targetName}", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);

                var stopwatch = Stopwatch.StartNew();
                var results = await index.SearchAsync(new SearchQuery("invoice 2026", SearchMode.FilesAndFolders, limit: 10), CancellationToken.None);
                stopwatch.Stop();

                Assert.Equal(targetName, results[0].Record.Name);
                Assert.True(
                    stopwatch.Elapsed < budget,
                    $"Expected large-index search to complete under {budget.TotalSeconds:N0}s; elapsed {stopwatch.Elapsed}. This loose budget is diagnostic and intentionally avoids failing under ordinary CI load.");
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchReturnsHighQualityFuzzyMatchAfterManyShorterWeakCandidates()
    {
        var dbPath = CreateTempDbPath();
        const string weakName = "Aivx26-0000.txt";
        const string targetName = "Invoice 2026.xlsx";

        Assert.True(
            FuzzyMatcher.Score("invoice", targetName) > FuzzyMatcher.Score("invoice", weakName),
            "The target fixture must score higher than the shorter weak fuzzy filler rows.");

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await InsertShorterWeakIv26RowsAsync(index, 1_001);
                await index.UpsertAsync(FileRecord.Create($"C:\\Docs\\{targetName}", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);

                var results = await index.SearchAsync(new SearchQuery("invoice", SearchMode.FilesAndFolders, limit: 10), CancellationToken.None);

                Assert.Equal(targetName, results[0].Record.Name);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchReturnsHighQualityFuzzyMatchAfterManySameBucketWeakCandidates()
    {
        var dbPath = CreateTempDbPath();
        const string weakName = "Ivx26-0000.txt";
        const string targetName = "Z-Invoice 2026.xlsx";

        Assert.True(
            FuzzyMatcher.Score("invoice", targetName) > FuzzyMatcher.Score("invoice", weakName),
            "The target fixture must score higher than the same-bucket weak fuzzy filler rows.");

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await InsertSameBucketWeakIv26RowsAsync(index, 250);
                await index.UpsertAsync(FileRecord.Create($"C:\\Docs\\{targetName}", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);

                var results = await index.SearchAsync(new SearchQuery("invoice", SearchMode.FilesAndFolders, limit: 10), CancellationToken.None);

                Assert.Equal(targetName, results[0].Record.Name);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchReturnsHighQualitySubstringMatchAfterMoreThanFiveThousandEarlierWeakFuzzyCandidates()
    {
        var dbPath = CreateTempDbPath();
        const string weakName = "Aaaaaiaaaavaaaa2aaaa6-fragment-0000.txt";
        const string targetName = "ZInvoice 2026.xlsx";

        Assert.True(
            FuzzyMatcher.Score("invoice 2026", targetName) > FuzzyMatcher.Score("invoice 2026", weakName),
            "The target fixture must score higher than the earlier fuzzy filler rows.");

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await InsertAlphabeticallyEarlierWeakIv26RowsAsync(index);
                await index.UpsertAsync(FileRecord.Create($"C:\\Docs\\{targetName}", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);

                var results = await index.SearchAsync(new SearchQuery("invoice 2026", SearchMode.FilesAndFolders), CancellationToken.None);

                Assert.Equal(targetName, results[0].Record.Name);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public void SearchSkipsExpensiveFuzzyCandidatePassesForOneOrTwoCharacterQueries()
    {
        Assert.False(SqliteSearchIndex.UsesExpensiveFuzzyCandidatesForTests(new SearchQuery("a", SearchMode.FilesAndFolders)));
        Assert.False(SqliteSearchIndex.UsesExpensiveFuzzyCandidatesForTests(new SearchQuery("ext:txt a", SearchMode.FilesAndFolders)));
        Assert.False(SqliteSearchIndex.UsesExpensiveFuzzyCandidatesForTests(new SearchQuery("ab", SearchMode.FilesAndFolders)));
        Assert.True(SqliteSearchIndex.UsesExpensiveFuzzyCandidatesForTests(new SearchQuery("abc", SearchMode.FilesAndFolders)));
    }

    private static async Task InsertAlphabeticallyEarlierRowsAsync(SqliteSearchIndex index)
    {
        var lastWriteTime = DateTimeOffset.UtcNow;
        var records = Enumerable
            .Range(0, EarlierRowCount)
            .Select(i => FileRecord.Create($"C:\\Docs\\A{i:D4}.txt", false, 1, lastWriteTime));

        await index.UpsertManyAsync(records, CancellationToken.None);
    }

    private static async Task InsertMatchingInvoiceFilesAsync(SqliteSearchIndex index, int count)
    {
        var lastWriteTime = DateTimeOffset.UtcNow;
        var records = Enumerable
            .Range(0, count)
            .Select(i => FileRecord.Create($"C:\\Docs\\Invoice{i:D4}.txt", false, 1, lastWriteTime));

        await index.UpsertManyAsync(records, CancellationToken.None);
    }

    private static IEnumerable<FileRecord> CreateRecordsThatHoldEnumeration(
        ManualResetEventSlim enteredGate,
        ManualResetEventSlim releaseGate)
    {
        yield return FileRecord.Create("C:\\Docs\\HeldGateInvoice.xlsx", false, 1, DateTimeOffset.UtcNow);
        enteredGate.Set();
        releaseGate.Wait();
    }

    private static async Task InsertAlphabeticallyEarlierFuzzyInvoiceRowsAsync(SqliteSearchIndex index)
    {
        var lastWriteTime = DateTimeOffset.UtcNow;
        var records = Enumerable
            .Range(0, EarlierRowCount)
            .Select(i => FileRecord.Create($"C:\\Docs\\A-i-n-v-o-i-c-e-fragment-{i:D4}.txt", false, 1, lastWriteTime));

        await index.UpsertManyAsync(records, CancellationToken.None);
    }

    private static async Task InsertAlphabeticallyEarlierWeakIv26RowsAsync(SqliteSearchIndex index)
    {
        await InsertWeakIv26RowsAsync(index, EarlierRowCount);
    }

    private static async Task InsertShorterWeakIv26RowsAsync(SqliteSearchIndex index, int count)
    {
        var lastWriteTime = DateTimeOffset.UtcNow;
        var records = Enumerable
            .Range(0, count)
            .Select(i => FileRecord.Create($"C:\\Docs\\Aivx26-{i:D4}.txt", false, 1, lastWriteTime));

        await index.UpsertManyAsync(records, CancellationToken.None);
    }

    private static async Task InsertSameBucketWeakIv26RowsAsync(SqliteSearchIndex index, int count)
    {
        var lastWriteTime = DateTimeOffset.UtcNow;
        var records = Enumerable
            .Range(0, count)
            .Select(i => FileRecord.Create($"C:\\Docs\\Ivx26-{i:D4}.txt", false, 1, lastWriteTime));

        await index.UpsertManyAsync(records, CancellationToken.None);
    }

    private static async Task InsertWeakIv26RowsAsync(SqliteSearchIndex index, int count)
    {
        var lastWriteTime = DateTimeOffset.UtcNow;
        var records = Enumerable
            .Range(0, count)
            .Select(i => FileRecord.Create($"C:\\Docs\\Aaaaaiaaaavaaaa2aaaa6-fragment-{i:D5}.txt", false, 1, lastWriteTime));

        await index.UpsertManyAsync(records, CancellationToken.None);
    }

    private static async Task CreateOldSchemaDatabaseAsync(string dbPath, FileRecord record)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        using var createCommand = connection.CreateCommand();
        createCommand.CommandText = """
            create table files(
                full_path text,
                path_key text primary key,
                name text,
                parent_path text,
                is_directory integer,
                size_bytes integer,
                last_write_time text
            );

            create table usage(
                full_path text,
                path_key text primary key,
                open_count integer,
                last_used_at text
            );
            """;
        await createCommand.ExecuteNonQueryAsync();

        using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText = """
            insert into files(
                full_path,
                path_key,
                name,
                parent_path,
                is_directory,
                size_bytes,
                last_write_time
            )
            values (
                $full_path,
                $path_key,
                $name,
                $parent_path,
                $is_directory,
                $size_bytes,
                $last_write_time
            );
            """;
        insertCommand.Parameters.AddWithValue("$full_path", record.FullPath);
        insertCommand.Parameters.AddWithValue("$path_key", record.PathKey);
        insertCommand.Parameters.AddWithValue("$name", record.Name);
        insertCommand.Parameters.AddWithValue("$parent_path", record.ParentPath);
        insertCommand.Parameters.AddWithValue("$is_directory", record.IsDirectory ? 1 : 0);
        insertCommand.Parameters.AddWithValue("$size_bytes", record.SizeBytes);
        insertCommand.Parameters.AddWithValue("$last_write_time", record.LastWriteTime.ToString("O"));

        await insertCommand.ExecuteNonQueryAsync();
    }

    private static async Task CreateCurrentSchemaDatabaseWithoutMetadataAsync(string dbPath, FileRecord record)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        using var createCommand = connection.CreateCommand();
        createCommand.CommandText = """
            create table files(
                full_path text not null,
                path_key text not null primary key,
                name text not null,
                parent_path text not null,
                search_text text not null,
                is_directory integer not null check(is_directory in (0, 1)),
                size_bytes integer not null check(size_bytes >= 0),
                last_write_time text not null
            );

            create table usage(
                full_path text not null,
                path_key text not null primary key,
                open_count integer not null check(open_count >= 0),
                last_used_at text not null
            );
            """;
        await createCommand.ExecuteNonQueryAsync();

        using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText = """
            insert into files(
                full_path,
                path_key,
                name,
                parent_path,
                search_text,
                is_directory,
                size_bytes,
                last_write_time
            )
            values (
                $full_path,
                $path_key,
                $name,
                $parent_path,
                $search_text,
                $is_directory,
                $size_bytes,
                $last_write_time
            );
            """;
        insertCommand.Parameters.AddWithValue("$full_path", record.FullPath);
        insertCommand.Parameters.AddWithValue("$path_key", record.PathKey);
        insertCommand.Parameters.AddWithValue("$name", record.Name);
        insertCommand.Parameters.AddWithValue("$parent_path", record.ParentPath);
        insertCommand.Parameters.AddWithValue("$search_text", record.Name);
        insertCommand.Parameters.AddWithValue("$is_directory", record.IsDirectory ? 1 : 0);
        insertCommand.Parameters.AddWithValue("$size_bytes", record.SizeBytes);
        insertCommand.Parameters.AddWithValue("$last_write_time", record.LastWriteTime.ToString("O"));

        await insertCommand.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlySet<string>> ReadIndexNamesAsync(string dbPath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = """
            select name
            from sqlite_master
            where type = 'index';
            """;

        var indexNames = new HashSet<string>(StringComparer.Ordinal);
        var reader = await command.ExecuteReaderAsync();
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync())
            {
                indexNames.Add(reader.GetString(0));
            }
        }

        return indexNames;
    }

    private static async Task<long> CountRowsAsync(string dbPath, string tableName)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = $"select count(*) from {tableName};";

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<long> CountMatchingFilesAsync(string dbPath, string pathKey)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from files where path_key = $path_key;";
        command.Parameters.AddWithValue("$path_key", pathKey);

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<(int OpenCount, DateTimeOffset LastUsedAt)> ReadUsageRowAsync(
        string dbPath,
        string pathKey)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = "select open_count, last_used_at from usage where path_key = $path_key;";
        command.Parameters.AddWithValue("$path_key", pathKey);

        var reader = await command.ExecuteReaderAsync();
        await using (reader.ConfigureAwait(false))
        {
            Assert.True(await reader.ReadAsync());
            return (reader.GetInt32(0), DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture));
        }
    }

    private static IEnumerable<(string FullPath, int OpenCount, DateTimeOffset LastUsedAt)> CreateUnrelatedUsageRows(
        int count,
        DateTimeOffset lastUsedAt)
    {
        for (var i = 0; i < count; i++)
        {
            yield return ($"C:\\Unrelated\\Unused-{i:D5}.txt", 1, lastUsedAt);
        }
    }

    private static async Task InsertUsageRowsAsync(
        string dbPath,
        IEnumerable<(string FullPath, int OpenCount, DateTimeOffset LastUsedAt)> rows)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            insert into usage(full_path, path_key, open_count, last_used_at)
            values ($full_path, $path_key, $open_count, $last_used_at);
            """;
        var fullPathParameter = command.Parameters.Add("$full_path", SqliteType.Text);
        var pathKeyParameter = command.Parameters.Add("$path_key", SqliteType.Text);
        var openCountParameter = command.Parameters.Add("$open_count", SqliteType.Integer);
        var lastUsedAtParameter = command.Parameters.Add("$last_used_at", SqliteType.Text);

        foreach (var row in rows)
        {
            var usage = new UsageRecord(row.FullPath, row.OpenCount, row.LastUsedAt);
            fullPathParameter.Value = usage.FullPath;
            pathKeyParameter.Value = usage.PathKey;
            openCountParameter.Value = usage.OpenCount;
            lastUsedAtParameter.Value = usage.LastUsedAt.ToString("O");

            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task AssertSqliteConstraintAsync(string dbPath, string commandText)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = commandText;

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }

    private static string CreateTempDbPath()
    {
        return Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => _postCount;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }
    }
}

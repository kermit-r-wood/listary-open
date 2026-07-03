using System.Diagnostics;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Core.Usage;
using ListaryOpen.Infrastructure.Search;
using Microsoft.Data.Sqlite;

namespace ListaryOpen.Infrastructure.Tests.Search;

public sealed class SqliteSearchIndexTests
{
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
    public async Task SearchReturnsFuzzyMatchOutsideFirstFiveThousandNames()
    {
        var dbPath = CreateTempDbPath();

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await InsertAlphabeticallyEarlierRowsAsync(index);
                await index.UpsertAsync(FileRecord.Create("C:\\Docs\\ZTargetInvoice.xlsx", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);

                var results = await index.SearchAsync(new SearchQuery("zti", SearchMode.FilesAndFolders), CancellationToken.None);

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
    public async Task OpenMigratesOldSchemaDatabaseBeforeSearchAndUpsert()
    {
        var dbPath = CreateTempDbPath();
        var oldRecord = FileRecord.Create("C:\\Docs\\OldInvoice.xlsx", false, 10, DateTimeOffset.UtcNow);

        try
        {
            await CreateOldSchemaDatabaseAsync(dbPath, oldRecord);

            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                var oldResults = await index.SearchAsync(new SearchQuery("oldinvoice", SearchMode.FilesAndFolders), CancellationToken.None);

                Assert.Equal("OldInvoice.xlsx", Assert.Single(oldResults).Record.Name);

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
                Assert.Equal("usage", results[0].MatchReason);
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
        var budget = TimeSpan.FromSeconds(3);

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await InsertWeakIv26RowsAsync(index, fillerCount);
                await index.UpsertAsync(FileRecord.Create($"C:\\Docs\\{targetName}", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);

                var stopwatch = Stopwatch.StartNew();
                var results = await index.SearchAsync(new SearchQuery("iv26", SearchMode.FilesAndFolders, limit: 10), CancellationToken.None);
                stopwatch.Stop();

                Assert.Equal(targetName, results[0].Record.Name);
                Assert.True(
                    stopwatch.Elapsed < budget,
                    $"Expected large-index search to complete under {budget.TotalSeconds:N0}s; elapsed {stopwatch.Elapsed}. The budget is intentionally conservative for CI while guarding against unbounded candidate scans.");
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    [Fact]
    public async Task SearchReturnsHighQualityAbbreviationMatchAfterMoreThanFiveThousandEarlierWeakFuzzyCandidates()
    {
        var dbPath = CreateTempDbPath();
        const string weakName = "Aaaaaiaaaavaaaa2aaaa6-fragment-0000.txt";
        const string targetName = "ZInvoice 2026.xlsx";

        Assert.True(
            FuzzyMatcher.Score("iv26", targetName) > FuzzyMatcher.Score("iv26", weakName),
            "The target fixture must score higher than the earlier fuzzy filler rows.");

        try
        {
            await using (var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None))
            {
                await InsertAlphabeticallyEarlierWeakIv26RowsAsync(index);
                await index.UpsertAsync(FileRecord.Create($"C:\\Docs\\{targetName}", false, 10, DateTimeOffset.UtcNow), CancellationToken.None);

                var results = await index.SearchAsync(new SearchQuery("iv26", SearchMode.FilesAndFolders), CancellationToken.None);

                Assert.Equal(targetName, results[0].Record.Name);
            }
        }
        finally
        {
            DeleteIfExists(dbPath);
        }
    }

    private static async Task InsertAlphabeticallyEarlierRowsAsync(SqliteSearchIndex index)
    {
        var lastWriteTime = DateTimeOffset.UtcNow;
        var records = Enumerable
            .Range(0, EarlierRowCount)
            .Select(i => FileRecord.Create($"C:\\Docs\\A{i:D4}.txt", false, 1, lastWriteTime));

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

using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
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

    private static async Task InsertAlphabeticallyEarlierRowsAsync(SqliteSearchIndex index)
    {
        var lastWriteTime = DateTimeOffset.UtcNow;
        var records = Enumerable
            .Range(0, EarlierRowCount)
            .Select(i => FileRecord.Create($"C:\\Docs\\A{i:D4}.txt", false, 1, lastWriteTime));

        await index.UpsertManyAsync(records, CancellationToken.None);
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
}

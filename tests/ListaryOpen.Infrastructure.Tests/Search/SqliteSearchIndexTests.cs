using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Search;

namespace ListaryOpen.Infrastructure.Tests.Search;

public sealed class SqliteSearchIndexTests
{
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

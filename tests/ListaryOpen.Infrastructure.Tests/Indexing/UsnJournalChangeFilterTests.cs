using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Indexing.Ntfs;
using ListaryOpen.Infrastructure.Search;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class UsnJournalChangeFilterTests
{
    [Fact]
    public async Task ExcludedJournalUpsertRemovesPreviouslyIndexedRecordAndAdvancesCheckpoint()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"listary-usn-filter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var dbPath = Path.Combine(directory, "index.db");
        var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
        try
        {
            var record = FileRecord.Create(
                Path.Combine(directory, "excluded.tmp"),
                isDirectory: false,
                sizeBytes: 10,
                DateTimeOffset.UtcNow);
            await index.UpsertAsync(record, CancellationToken.None);
            var checkpoint = new UsnJournalCheckpoint(
                directory,
                "NTFS",
                UsnJournalId: 7,
                NextUsn: 200,
                RulesVersion: SqliteSearchIndex.CurrentIndexContentVersion,
                LastFullScanAt: DateTimeOffset.UtcNow);

            var result = await UsnJournalChangeApplier.ApplyAsync(
                index,
                new[] { UsnJournalChange.Upsert(record) },
                checkpoint,
                CancellationToken.None,
                _ => false);

            var results = await index.SearchAsync(
                new SearchQuery("excluded.tmp", SearchMode.FilesAndFolders, limit: 20),
                CancellationToken.None);
            Assert.Empty(results);
            Assert.False(result.RequiresFullRescan);
            Assert.Equal(checkpoint, await index.ReadVolumeCheckpointAsync(directory, CancellationToken.None));
        }
        finally
        {
            await index.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }
}

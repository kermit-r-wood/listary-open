using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Indexing.Ntfs;
using ListaryOpen.Infrastructure.Search;

namespace ListaryOpen.Infrastructure.Tests.Search;

public sealed class HardLinkResyncIndexTests
{
    [Fact]
    public async Task HardLinkResyncRemovesStaleNamesAndKeepsLiveLinks()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "listary-hardlink-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            const ulong frn = 42;
            var now = DateTimeOffset.UtcNow;
            var a = FileRecord.Create(@"C:\Docs\a.txt", false, 10, now, frn);
            var a1 = FileRecord.Create(@"C:\Docs\a1.txt", false, 10, now, frn);
            var other = FileRecord.Create(@"C:\Docs\other.txt", false, 10, now);

            await index.UpsertManyAsync(new[] { a, a1, other }, CancellationToken.None);

            // After deleting a.txt hard-link, only a1 remains.
            var checkpoint = new UsnJournalCheckpoint(
                VolumeRoot: @"C:\",
                FileSystemName: "NTFS",
                UsnJournalId: 1,
                NextUsn: 100,
                RulesVersion: 1,
                LastFullScanAt: now);

            var apply = await UsnJournalChangeApplier.ApplyAsync(
                index,
                new[]
                {
                    UsnJournalChange.HardLinkResync(frn, new[] { a1 })
                },
                checkpoint,
                CancellationToken.None);

            Assert.False(apply.RequiresFullRescan);

            var results = await index.SearchAsync(
                new SearchQuery("a1", SearchMode.FilesAndFolders),
                CancellationToken.None);
            Assert.Contains(results, item => item.Record.FullPath.Equals(@"C:\Docs\a1.txt", StringComparison.OrdinalIgnoreCase));

            var stale = await index.SearchAsync(
                new SearchQuery("ext:txt a.txt", SearchMode.FilesAndFolders),
                CancellationToken.None);
            // exact name a.txt should not remain for the hard-linked FRN
            Assert.DoesNotContain(
                stale,
                item => item.Record.FullPath.Equals(@"C:\Docs\a.txt", StringComparison.OrdinalIgnoreCase));

            var stillOther = await index.SearchAsync(
                new SearchQuery("other", SearchMode.FilesAndFolders),
                CancellationToken.None);
            Assert.Contains(
                stillOther,
                item => item.Record.FullPath.Equals(@"C:\Docs\other.txt", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task HardLinkResyncWithCompanionDeleteRemovesFrnZeroGhostFromUsnUpsertPath()
    {
        // Real incremental path before FRN stamping (or mixed): names arrive via USN
        // upsert with FRN=0. HARD_LINK_CHANGE emits HardLinkResync(live) + Delete(usnPath).
        var dbPath = Path.Combine(Path.GetTempPath(), "listary-hardlink-frn0-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            const ulong frn = 77;
            var now = DateTimeOffset.UtcNow;

            // FRN=0 upserts as historical USN path would have written them.
            var a = FileRecord.Create(@"C:\Docs\a.txt", false, 10, now, fileReferenceNumber: 0);
            var a1 = FileRecord.Create(@"C:\Docs\a1.txt", false, 10, now, fileReferenceNumber: 0);
            var other = FileRecord.Create(@"C:\Docs\other.txt", false, 10, now, fileReferenceNumber: 0);
            await index.UpsertManyAsync(new[] { a, a1, other }, CancellationToken.None);

            var checkpoint = new UsnJournalCheckpoint(
                VolumeRoot: @"C:\",
                FileSystemName: "NTFS",
                UsnJournalId: 1,
                NextUsn: 200,
                RulesVersion: 1,
                LastFullScanAt: now);

            // Producer: live names only a1; companion Delete for removed USN path a.txt.
            var apply = await UsnJournalChangeApplier.ApplyAsync(
                index,
                new[]
                {
                    UsnJournalChange.HardLinkResync(
                        frn,
                        new[] { FileRecord.Create(@"C:\Docs\a1.txt", false, 10, now, frn) }),
                    UsnJournalChange.Delete(@"C:\Docs\a.txt")
                },
                checkpoint,
                CancellationToken.None);

            Assert.False(apply.RequiresFullRescan);

            var live = await index.SearchAsync(
                new SearchQuery("a1", SearchMode.FilesAndFolders),
                CancellationToken.None);
            Assert.Contains(
                live,
                item => item.Record.FullPath.Equals(@"C:\Docs\a1.txt", StringComparison.OrdinalIgnoreCase));

            var stale = await index.SearchAsync(
                new SearchQuery("a.txt", SearchMode.FilesAndFolders),
                CancellationToken.None);
            Assert.DoesNotContain(
                stale,
                item => item.Record.FullPath.Equals(@"C:\Docs\a.txt", StringComparison.OrdinalIgnoreCase));

            var stillOther = await index.SearchAsync(
                new SearchQuery("other", SearchMode.FilesAndFolders),
                CancellationToken.None);
            Assert.Contains(
                stillOther,
                item => item.Record.FullPath.Equals(@"C:\Docs\other.txt", StringComparison.OrdinalIgnoreCase));

            // FRN must have been stamped on a1: a second empty resync purges by file_reference alone.
            var purge = await UsnJournalChangeApplier.ApplyAsync(
                index,
                new[] { UsnJournalChange.HardLinkResync(frn, Array.Empty<FileRecord>()) },
                checkpoint with { NextUsn = 201 },
                CancellationToken.None);
            Assert.False(purge.RequiresFullRescan);

            var afterPurge = await index.SearchAsync(
                new SearchQuery("a1", SearchMode.FilesAndFolders),
                CancellationToken.None);
            Assert.DoesNotContain(
                afterPurge,
                item => item.Record.FullPath.Equals(@"C:\Docs\a1.txt", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* ignore */ }
        }
    }
}

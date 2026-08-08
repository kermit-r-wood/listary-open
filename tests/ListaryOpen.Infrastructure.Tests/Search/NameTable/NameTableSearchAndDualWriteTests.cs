using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Indexing.Ntfs;
using ListaryOpen.Infrastructure.Search;
using ListaryOpen.Infrastructure.Search.NameTable;

namespace ListaryOpen.Infrastructure.Tests.Search.NameTable;

public sealed class NameTableSearchAndDualWriteTests
{
    [Fact]
    public async Task FullPinyinLaneKeepsInsertionSampleBeforeFinalPathRanking()
    {
        await using var index = new NameTableSearchIndex();
        var records = new List<FileRecord>(3_000);
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var id = 0; id < 1_500; id++)
        {
            records.Add(FileRecord.Create(
                $@"C:\项\{id:D5}\合同.txt",
                false,
                1,
                timestamp,
                (ulong)(id * 2 + 1)));
            records.Add(FileRecord.Create(
                $@"C:\Generated\{id:D5}\合同.txt",
                false,
                1,
                timestamp,
                (ulong)(id * 2 + 2)));
        }

        await index.UpsertManyAsync(records, CancellationToken.None);
        var results = await index.SearchAsync(
            new SearchQuery("hetong", SearchMode.FilesAndFolders, limit: 50),
            CancellationToken.None);

        Assert.Equal(50, results.Count);
        Assert.StartsWith(@"C:\Generated\", results[0].Record.FullPath, StringComparison.Ordinal);
        Assert.All(results, result => Assert.Equal("pinyin", result.MatchReason));
    }

    [Fact]
    public async Task IndexedTermsStayContiguousBeforeBoundedFuzzyFallback()
    {
        await using var index = new NameTableSearchIndex();
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var records = Enumerable.Range(0, 250)
            .Select(id => FileRecord.Create(
                $@"C:\Corpus\a-filler-{id:D4}.txt",
                false,
                1,
                timestamp,
                (ulong)(id + 1)))
            .Append(FileRecord.Create(
                @"C:\Corpus\invoice-0002026-2025.txt",
                false,
                1,
                timestamp,
                1_000))
            .Append(FileRecord.Create(
                @"C:\Corpus\z-invoice-2025-026.txt",
                false,
                1,
                timestamp,
                1_001))
            .ToArray();

        await index.UpsertManyAsync(records, CancellationToken.None);
        var results = await index.SearchAsync(
            new SearchQuery("2026 invoice", SearchMode.FilesAndFolders, limit: 50),
            CancellationToken.None);

        var only = Assert.Single(results);
        Assert.Equal("invoice-0002026-2025.txt", only.Record.Name);
    }

    [Fact]
    public async Task PathOperatorKeepsOrderedUnicodeAncestorMatching()
    {
        await using var index = new NameTableSearchIndex();
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await index.UpsertManyAsync(
            [
                FileRecord.Create(@"C:\Corpus\Projects\中大成赢", true, 0, timestamp, 1),
                FileRecord.Create(@"C:\Corpus\Projects\中大成赢\门房图纸.cad", false, 1, timestamp, 2),
                FileRecord.Create(@"C:\Corpus\Other\门房图纸.cad", false, 1, timestamp, 3)
            ],
            CancellationToken.None);

        var results = await index.SearchAsync(
            new SearchQuery(@"path:Projects\中大 图纸", SearchMode.FilesAndFolders, limit: 50),
            CancellationToken.None);

        var only = Assert.Single(results);
        Assert.Equal(@"C:\Corpus\Projects\中大成赢\门房图纸.cad", only.Record.FullPath);
    }

    [Fact]
    public async Task NameTableSearchReturnsSeededPathsThroughShippedSearchApi()
    {
        await using var index = new NameTableSearchIndex();
        await index.UpsertAsync(
            FileRecord.Create(@"C:\Workspace\ListaryOpen\readme.md", false, 10, DateTimeOffset.UtcNow),
            CancellationToken.None);
        await index.UpsertAsync(
            FileRecord.Create(@"C:\Workspace\other\note.txt", false, 10, DateTimeOffset.UtcNow),
            CancellationToken.None);

        var results = await index.SearchAsync(
            new SearchQuery("ListaryOpen", SearchMode.FilesAndFolders),
            CancellationToken.None);

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.Record.FullPath.Contains("ListaryOpen", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MutationOnlyUsnLeavesCheckpointUnchanged()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "listary-open-nt-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            var root = @"C:\VolRoot";
            var before = new UsnJournalCheckpoint(
                root,
                "NTFS",
                9,
                100,
                SqliteSearchIndex.CurrentIndexContentVersion,
                DateTimeOffset.UtcNow);
            await index.SaveVolumeCheckpointAsync(before, CancellationToken.None);

            await index.ApplyUsnMutationsAsync(
                new[]
                {
                    UsnJournalIndexChange.Upsert(
                        FileRecord.Create(Path.Combine(root, "file.txt"), false, 1, DateTimeOffset.UtcNow))
                },
                CancellationToken.None);

            var after = await index.ReadVolumeCheckpointAsync(root, CancellationToken.None);
            Assert.NotNull(after);
            Assert.Equal(100, after!.NextUsn);
            Assert.Equal(9ul, after.UsnJournalId);

            var found = await index.SearchAsync(
                new SearchQuery("file", SearchMode.FilesAndFolders),
                CancellationToken.None);
            Assert.Contains(found, r => r.Record.Name.Equals("file.txt", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    [Fact]
    public async Task NameTableUsnAppliesAndPersistsDurableLosnWatermark()
    {
        var snapPath = Path.Combine(Path.GetTempPath(), "listary-open-nt-" + Guid.NewGuid().ToString("N") + ".losn");
        try
        {
            await using var nameTable = await NameTableSearchIndex.OpenAsync(snapPath, CancellationToken.None);
            var root = @"C:\NtVol";
            var checkpoint = new UsnJournalCheckpoint(
                root,
                "NTFS",
                11,
                250,
                SqliteSearchIndex.CurrentIndexContentVersion,
                DateTimeOffset.UtcNow);

            await nameTable.ApplyUsnMutationsAsync(
                new[]
                {
                    UsnJournalIndexChange.Upsert(
                        FileRecord.Create(Path.Combine(root, "alpha.txt"), false, 1, DateTimeOffset.UtcNow))
                },
                CancellationToken.None);
            var saved = await nameTable.SaveDurableSnapshotAsync(checkpoint, CancellationToken.None);

            Assert.True(saved);
            Assert.True(File.Exists(snapPath));
            Assert.Equal(250, nameTable.Engine.DurableWatermarkUsn);

            var fromNameTable = await nameTable.SearchAsync(
                new SearchQuery("alpha", SearchMode.FilesAndFolders),
                CancellationToken.None);
            Assert.Contains(fromNameTable, r => r.Record.Name == "alpha.txt");

            // Reopen from LOSN restores watermark + records.
            await using var reopened = await NameTableSearchIndex.OpenAsync(snapPath, CancellationToken.None);
            Assert.Equal(250, reopened.Engine.DurableWatermarkUsn);
            var reopenedHits = await reopened.SearchAsync(
                new SearchQuery("alpha", SearchMode.FilesAndFolders),
                CancellationToken.None);
            Assert.Contains(reopenedHits, r => r.Record.Name == "alpha.txt");
        }
        finally
        {
            foreach (var path in new[] { snapPath, snapPath + ".bak", snapPath + ".tmp" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task NameTableUsnMutationFailureDoesNotAdvanceDurableWatermark()
    {
        var snapPath = Path.Combine(Path.GetTempPath(), "listary-open-fail-" + Guid.NewGuid().ToString("N") + ".losn");
        try
        {
            await using var nameTable = await NameTableSearchIndex.OpenAsync(snapPath, CancellationToken.None);
            nameTable.Engine.FailNextApplyUsnMutationsForTests();

            var root = @"C:\FailVol";
            var ex = await Assert.ThrowsAsync<IOException>(() =>
                nameTable.ApplyUsnMutationsAsync(
                    new[]
                    {
                        UsnJournalIndexChange.Upsert(
                            FileRecord.Create(Path.Combine(root, "x.txt"), false, 1, DateTimeOffset.UtcNow))
                    },
                    CancellationToken.None));

            Assert.Contains("Injected NameTable", ex.Message, StringComparison.Ordinal);
            Assert.Equal(0, nameTable.Engine.DurableWatermarkUsn);
            Assert.False(File.Exists(snapPath));
        }
        finally
        {
            foreach (var path in new[] { snapPath, snapPath + ".bak", snapPath + ".tmp" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    [Fact]
    public async Task SaveDurableSnapshotReturnsFalseWithoutLosnPathAndDoesNotClaimWatermark()
    {
        // Memory-only NameTable (no LOSN path) — not durable.
        await using var nameTable = new NameTableSearchIndex(snapshotPath: null);
        var root = @"C:\NoDur";

        await nameTable.ApplyUsnMutationsAsync(
            new[]
            {
                UsnJournalIndexChange.Upsert(
                    FileRecord.Create(Path.Combine(root, "y.txt"), false, 1, DateTimeOffset.UtcNow))
            },
            CancellationToken.None);

        var saved = await nameTable.SaveDurableSnapshotAsync(
            new UsnJournalCheckpoint(
                root,
                "NTFS",
                2,
                50,
                SqliteSearchIndex.CurrentIndexContentVersion,
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        Assert.False(saved);
        Assert.Equal(0, nameTable.Engine.DurableWatermarkUsn);
        // Session search still sees the mutation.
        var hits = await nameTable.SearchAsync(
            new SearchQuery("y", SearchMode.FilesAndFolders),
            CancellationToken.None);
        Assert.Contains(hits, r => r.Record.Name == "y.txt");
    }

    [Fact]
    public async Task LosnSnapshotRoundTripRestoresRecordsAndWatermark()
    {
        var path = Path.Combine(Path.GetTempPath(), "listary-open-losn-" + Guid.NewGuid().ToString("N") + ".losn");
        try
        {
            var engine = new NameTableEngine();
            engine.Upsert(FileRecord.Create(@"C:\Snap\a.txt", false, 3, DateTimeOffset.UtcNow));
            engine.SetDurableWatermark(@"C:\Snap", 777);
            var store = new LosnSnapshotStore();
            await store.SaveAsync(path, engine, CancellationToken.None);

            var loaded = new NameTableEngine();
            await store.LoadIntoAsync(path, loaded, CancellationToken.None);
            Assert.Equal(1, loaded.LiveCount);
            Assert.Equal(777, loaded.DurableWatermarkUsn);
            Assert.Equal(@"C:\Snap", loaded.DurableWatermarkVolume);
            Assert.Equal(500L, LosnSnapshotStore.ResumeUsn(snapshotWatermarkUsn: 500, metaCheckpointUsn: 900));
        }
        finally
        {
            foreach (var p in new[] { path, path + ".bak", path + ".tmp" })
            {
                if (File.Exists(p))
                {
                    File.Delete(p);
                }
            }
        }
    }

    [Fact]
    public async Task StagedFullBuildIsInvisibleUntilCommitAndAbortKeepsOldEpoch()
    {
        await using var index = new NameTableSearchIndex();
        await index.UpsertAsync(
            FileRecord.Create(@"C:\Root\old.txt", false, 1, DateTimeOffset.UtcNow),
            CancellationToken.None);
        await index.UpsertAsync(
            FileRecord.Create(@"D:\Other\keep.txt", false, 1, DateTimeOffset.UtcNow),
            CancellationToken.None);

        await index.BeginFullBuildRootAsync(@"C:\Root", CancellationToken.None);
        await index.UpsertManyAsync(
            [FileRecord.Create(@"C:\Root\new.txt", false, 2, DateTimeOffset.UtcNow)],
            CancellationToken.None);

        Assert.NotEmpty(await index.SearchAsync(
            new SearchQuery("old", SearchMode.FilesAndFolders),
            CancellationToken.None));
        Assert.Empty(await index.SearchAsync(
            new SearchQuery("new", SearchMode.FilesAndFolders),
            CancellationToken.None));

        await index.AbortFullBuildRootAsync(CancellationToken.None);
        Assert.NotEmpty(await index.SearchAsync(
            new SearchQuery("old", SearchMode.FilesAndFolders),
            CancellationToken.None));

        await index.BeginFullBuildRootAsync(@"C:\Root", CancellationToken.None);
        await index.UpsertManyAsync(
            [FileRecord.Create(@"C:\Root\new.txt", false, 2, DateTimeOffset.UtcNow)],
            CancellationToken.None);
        await index.CommitFullBuildRootAsync(@"C:\Root", CancellationToken.None);

        Assert.Empty(await index.SearchAsync(
            new SearchQuery("old", SearchMode.FilesAndFolders),
            CancellationToken.None));
        Assert.NotEmpty(await index.SearchAsync(
            new SearchQuery("new", SearchMode.FilesAndFolders),
            CancellationToken.None));
        Assert.NotEmpty(await index.SearchAsync(
            new SearchQuery("keep", SearchMode.FilesAndFolders),
            CancellationToken.None));
    }

    [Fact]
    public async Task LosnV2PersistsIndependentRootCheckpoints()
    {
        var path = Path.Combine(Path.GetTempPath(), "listary-open-losn-multi-" + Guid.NewGuid().ToString("N") + ".losn");
        try
        {
            await using (var index = await NameTableSearchIndex.OpenAsync(path, CancellationToken.None))
            {
                await index.UpsertAsync(
                    FileRecord.Create(@"C:\One\a.txt", false, 1, DateTimeOffset.UtcNow),
                    CancellationToken.None);
                await index.SaveDurableSnapshotAsync(
                    new UsnJournalCheckpoint(@"C:\One", "NTFS", 11, 101, 7, DateTimeOffset.UtcNow),
                    CancellationToken.None);
                await index.UpsertAsync(
                    FileRecord.Create(@"D:\Two\b.txt", false, 1, DateTimeOffset.UtcNow),
                    CancellationToken.None);
                await index.SaveDurableSnapshotAsync(
                    new UsnJournalCheckpoint(@"D:\Two", "NTFS", 22, 202, 7, DateTimeOffset.UtcNow),
                    CancellationToken.None);
            }

            await using var reopened = await NameTableSearchIndex.OpenAsync(path, CancellationToken.None);
            Assert.True(reopened.Engine.TryGetDurableCheckpoint(@"C:\One", out var first));
            Assert.True(reopened.Engine.TryGetDurableCheckpoint(@"D:\Two", out var second));
            Assert.Equal(101, first!.NextUsn);
            Assert.Equal(11ul, first.UsnJournalId);
            Assert.Equal(202, second!.NextUsn);
            Assert.Equal(22ul, second.UsnJournalId);
        }
        finally
        {
            foreach (var candidate in new[] { path, path + ".bak", path + ".tmp" })
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
        }
    }

    [Fact]
    public async Task FailedLosnWriteDoesNotAdvanceDurableCheckpoint()
    {
        var path = Path.Combine(Path.GetTempPath(), "listary-open-losn-fail-" + Guid.NewGuid().ToString("N") + ".losn");
        try
        {
            await using var index = await NameTableSearchIndex.OpenAsync(path, CancellationToken.None);
            await index.UpsertAsync(
                FileRecord.Create(@"C:\Fail\a.txt", false, 1, DateTimeOffset.UtcNow),
                CancellationToken.None);
            await index.SaveDurableSnapshotAsync(
                new UsnJournalCheckpoint(@"C:\Fail", "NTFS", 1, 100, 7, DateTimeOffset.UtcNow),
                CancellationToken.None);

            await using (var heldTemp = new FileStream(
                             path + ".tmp",
                             FileMode.Create,
                             FileAccess.ReadWrite,
                             FileShare.None))
            {
                await Assert.ThrowsAsync<IOException>(() => index.SaveDurableSnapshotAsync(
                    new UsnJournalCheckpoint(@"C:\Fail", "NTFS", 1, 200, 7, DateTimeOffset.UtcNow),
                    CancellationToken.None));
            }

            Assert.True(index.Engine.TryGetDurableCheckpoint(@"C:\Fail", out var durable));
            Assert.Equal(100, durable!.NextUsn);
        }
        finally
        {
            foreach (var candidate in new[] { path, path + ".bak", path + ".tmp" })
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
        }
    }

    [Fact]
    public async Task CorruptPrimaryLosnFallsBackToBackup()
    {
        var path = Path.Combine(Path.GetTempPath(), "listary-open-losn-backup-" + Guid.NewGuid().ToString("N") + ".losn");
        try
        {
            await using (var index = await NameTableSearchIndex.OpenAsync(path, CancellationToken.None))
            {
                await index.UpsertAsync(
                    FileRecord.Create(@"C:\Backup\first.txt", false, 1, DateTimeOffset.UtcNow),
                    CancellationToken.None);
                await index.SaveDurableSnapshotAsync(
                    new UsnJournalCheckpoint(@"C:\Backup", "NTFS", 1, 100, 7, DateTimeOffset.UtcNow),
                    CancellationToken.None);
                await index.UpsertAsync(
                    FileRecord.Create(@"C:\Backup\second.txt", false, 1, DateTimeOffset.UtcNow),
                    CancellationToken.None);
                await index.SaveDurableSnapshotAsync(
                    new UsnJournalCheckpoint(@"C:\Backup", "NTFS", 1, 200, 7, DateTimeOffset.UtcNow),
                    CancellationToken.None);
            }

            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
            await using var reopened = await NameTableSearchIndex.OpenAsync(path, CancellationToken.None);
            Assert.True(reopened.Engine.TryGetDurableCheckpoint(@"C:\Backup", out var checkpoint));
            Assert.Equal(100, checkpoint!.NextUsn);
            Assert.NotEmpty(await reopened.SearchAsync(
                new SearchQuery("first", SearchMode.FilesAndFolders),
                CancellationToken.None));
            Assert.Empty(await reopened.SearchAsync(
                new SearchQuery("second", SearchMode.FilesAndFolders),
                CancellationToken.None));
        }
        finally
        {
            foreach (var candidate in new[] { path, path + ".bak", path + ".tmp" })
            {
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                }
            }
        }
    }
}

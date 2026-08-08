using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Infrastructure.Indexing.Ntfs;
using ListaryOpen.Infrastructure.Search.NameTable;

namespace ListaryOpen.Infrastructure.Tests.Search.NameTable;

public sealed class NameTableEngineTests
{
    [Fact]
    public void CompactTrigramFindsUtf8Substring()
    {
        var names = new CompactNameStore();
        var nameId = names.Add("门房图纸.cad");
        var index = CompactTrigramIndex.Build(names, [nameId]);

        Assert.True(index.TryGetRarestCandidatePositions(
            [System.Text.Encoding.UTF8.GetBytes("图纸")],
            out var positions));
        Assert.Equal([0], positions);
    }

    [Fact]
    public void UpsertSamePathKeepsStableIdentityAndUpdatesMetadata()
    {
        var engine = new NameTableEngine();
        var first = FileRecord.Create(@"C:\Docs\Invoice.txt", false, 10, DateTimeOffset.UtcNow, 42);
        var id1 = engine.Upsert(first);
        var second = FileRecord.Create(@"C:\Docs\Invoice.txt", false, 99, DateTimeOffset.UtcNow.AddMinutes(1), 42);
        var id2 = engine.Upsert(second);

        Assert.Equal(id1, id2);
        Assert.True(engine.TryGetByPathKey(first.PathKey, out var stored));
        Assert.Equal(99, stored!.SizeBytes);
        Assert.Equal(1, engine.LiveCount);
    }

    [Fact]
    public void HardLinkResyncReplacesAllNamesForFileReference()
    {
        var engine = new NameTableEngine();
        engine.Upsert(FileRecord.Create(@"C:\A\one.txt", false, 1, DateTimeOffset.UtcNow, 7));
        engine.Upsert(FileRecord.Create(@"C:\A\two.txt", false, 1, DateTimeOffset.UtcNow, 7));

        engine.HardLinkResync(
            7,
            new[]
            {
                FileRecord.Create(@"C:\A\live-only.txt", false, 2, DateTimeOffset.UtcNow, 7)
            });

        Assert.Equal(1, engine.LiveCount);
        Assert.True(engine.TryGetByPathKey(@"C:\A\live-only.txt".ToUpperInvariant(), out _));
        Assert.False(engine.TryGetByPathKey(@"C:\A\one.txt".ToUpperInvariant(), out _));
    }

    [Fact]
    public void ApplyUsnMutationsIsBlockedWhenDiverged()
    {
        var engine = new NameTableEngine();
        engine.MarkDiverged();
        Assert.Throws<InvalidOperationException>(() =>
            engine.ApplyUsnMutations(new[]
            {
                UsnJournalIndexChange.Upsert(
                    FileRecord.Create(@"C:\Docs\a.txt", false, 1, DateTimeOffset.UtcNow))
            }));
    }

    [Fact]
    public void CollectCandidatesFindsPrefixSubstringAndSnakeCase()
    {
        var engine = new NameTableEngine();
        engine.Upsert(FileRecord.Create(@"C:\Proj\ListaryOpen.App.exe", false, 1, DateTimeOffset.UtcNow));
        engine.Upsert(FileRecord.Create(@"C:\Proj\listary_open_hook.exe", false, 1, DateTimeOffset.UtcNow));
        engine.Upsert(FileRecord.Create(@"C:\Proj\mid-invoice-end.txt", false, 1, DateTimeOffset.UtcNow));

        var prefix = engine.CollectCandidates(new SearchQuery("ListaryOpen", SearchMode.FilesAndFolders), 50);
        Assert.Contains(prefix, r => r.Name.StartsWith("ListaryOpen", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(prefix, r => r.Name.StartsWith("listary_open", StringComparison.OrdinalIgnoreCase));

        var substr = engine.CollectCandidates(new SearchQuery("invoice", SearchMode.FilesAndFolders), 50);
        Assert.Contains(substr, r => r.Name.Contains("invoice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DeletePathAndDescendantsRemovesTree()
    {
        var engine = new NameTableEngine();
        engine.Upsert(FileRecord.Create(@"C:\Root", true, 0, DateTimeOffset.UtcNow));
        engine.Upsert(FileRecord.Create(@"C:\Root\child.txt", false, 1, DateTimeOffset.UtcNow));
        engine.Upsert(FileRecord.Create(@"C:\Other\x.txt", false, 1, DateTimeOffset.UtcNow));

        engine.DeletePathAndDescendants(@"C:\Root");
        Assert.Equal(1, engine.LiveCount);
        Assert.True(engine.TryGetByPathKey(@"C:\Other\x.txt".ToUpperInvariant(), out _));
    }
}

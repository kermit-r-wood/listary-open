using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;

namespace ListaryOpen.Core.Tests.Search;

public sealed class SearchQuickFilterTests
{
    [Fact]
    public void RankAppliesDirectoryExtensionAndModifiedDateFilters()
    {
        var now = DateTimeOffset.UtcNow;
        var records = new[]
        {
            FileRecord.Create("C:\\Docs\\target-new.jpg", false, 1, now),
            FileRecord.Create("C:\\Docs\\target-old.jpg", false, 1, now.AddDays(-20)),
            FileRecord.Create("C:\\Docs\\target-new.txt", false, 1, now),
            FileRecord.Create("C:\\Docs\\target-folder", true, 0, now)
        };
        var query = new SearchQuery(
            "target",
            SearchMode.FilesAndFolders,
            requiredExtensions: ["jpg", "png"],
            isDirectory: false,
            modifiedAfter: now.AddDays(-7));

        var result = Assert.Single(ResultRanker.Rank(query, records, [], []));

        Assert.Equal("target-new.jpg", result.Record.Name);
    }
}

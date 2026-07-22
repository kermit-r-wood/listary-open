using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class ConfiguredIndexFilterTests
{
    [Theory]
    [InlineData("C:\\Work\\node_modules\\pkg\\index.js", "node_modules")]
    [InlineData("C:\\Work\\cache\\file.tmp", "*.tmp")]
    [InlineData("C:\\Private\\secret.txt", "C:\\Private")]
    public void ExcludedPatternsRejectMatchingRecords(string path, string exclusion)
    {
        var record = FileRecord.Create(path, false, 1, DateTimeOffset.UtcNow);
        Assert.False(ConfiguredIndexFilter.ShouldInclude(record, [exclusion]));
    }

    [Fact]
    public void UnrelatedRecordRemainsIndexable()
    {
        var record = FileRecord.Create("C:\\Work\\src\\app.cs", false, 1, DateTimeOffset.UtcNow);
        Assert.True(ConfiguredIndexFilter.ShouldInclude(record, ["node_modules", "*.tmp", "C:\\Private"]));
    }
}

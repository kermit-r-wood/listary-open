using ListaryOpen.Core.Search;

namespace ListaryOpen.Core.Tests.Search;

public sealed class SearchQueryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void ConstructorRejectsBlankText(string? text)
    {
        Assert.Throws<ArgumentException>(() => new SearchQuery(text!, SearchMode.FilesAndFolders));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConstructorRejectsNonPositiveLimit(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchQuery("invoice", SearchMode.FilesAndFolders, limit));
    }

    [Fact]
    public void ConstructorCapsLimitAtMaximum()
    {
        var query = new SearchQuery("invoice", SearchMode.FilesAndFolders, 1_000);

        Assert.Equal(500, query.Limit);
    }
}

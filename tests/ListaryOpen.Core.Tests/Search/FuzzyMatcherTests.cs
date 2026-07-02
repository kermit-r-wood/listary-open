using ListaryOpen.Core.Search;

namespace ListaryOpen.Core.Tests.Search;

public sealed class FuzzyMatcherTests
{
    [Fact]
    public void ScoreRewardsContiguousNameMatch()
    {
        var score = FuzzyMatcher.Score("invoice", "Invoice-2026.xlsx");

        Assert.True(score > 80);
    }

    [Fact]
    public void ScoreSupportsAbbreviationMatch()
    {
        var score = FuzzyMatcher.Score("iv26", "Invoice 2026.xlsx");

        Assert.True(score > 20);
    }

    [Fact]
    public void ScoreReturnsZeroWhenCharactersAreMissing()
    {
        Assert.Equal(0, FuzzyMatcher.Score("xyz", "Invoice 2026.xlsx"));
    }
}

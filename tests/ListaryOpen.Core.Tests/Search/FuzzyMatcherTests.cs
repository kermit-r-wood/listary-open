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

    [Fact]
    public void ScoreRanksContiguousMatchAboveFragmentedSeparatorMatch()
    {
        var contiguous = FuzzyMatcher.Score("invoice", "Invoice.xlsx");
        var fragmented = FuzzyMatcher.Score("invoice", "i-n-v-o-i-c-e.xlsx");

        Assert.True(contiguous > fragmented);
    }

    [Fact]
    public void ScoreTreatsPathSeparatorsAsBoundaries()
    {
        var pathBoundary = FuzzyMatcher.Score("di", "C:\\Docs\\Invoice.pdf");
        var hyphenBoundary = FuzzyMatcher.Score("di", "C:-Docs-Invoice.pdf");

        Assert.True(pathBoundary > 0);
        Assert.Equal(hyphenBoundary, pathBoundary);
    }

    [Theory]
    [InlineData(null, "Invoice.xlsx")]
    [InlineData("", "Invoice.xlsx")]
    [InlineData(" ", "Invoice.xlsx")]
    [InlineData("invoice", null)]
    [InlineData("invoice", "")]
    [InlineData("invoice", " ")]
    public void ScoreReturnsZeroForBlankInputs(string? query, string? candidate)
    {
        Assert.Equal(0, FuzzyMatcher.Score(query!, candidate!));
    }
}

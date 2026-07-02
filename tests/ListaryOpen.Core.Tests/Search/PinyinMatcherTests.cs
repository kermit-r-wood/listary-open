using ListaryOpen.Core.Search;

namespace ListaryOpen.Core.Tests.Search;

public sealed class PinyinMatcherTests
{
    [Fact]
    public void ScoreMatchesKnownChinesePinyin()
    {
        var score = PinyinMatcher.Score("fapiao", "发票2026.xlsx");

        Assert.True(score > 50);
    }

    [Fact]
    public void ScoreMatchesKnownChineseInitials()
    {
        var score = PinyinMatcher.Score("fp", "发票2026.xlsx");

        Assert.True(score > 50);
    }

    [Fact]
    public void ScoreMatchesCommonChinesePinyin()
    {
        var score = PinyinMatcher.Score("hetong", "合同.docx");

        Assert.True(score > 50);
    }

    [Fact]
    public void ScoreMatchesCommonChineseInitials()
    {
        var score = PinyinMatcher.Score("ht", "合同.docx");

        Assert.True(score > 50);
    }

    [Theory]
    [InlineData(null, "发票2026.xlsx")]
    [InlineData("", "发票2026.xlsx")]
    [InlineData(" ", "发票2026.xlsx")]
    [InlineData("fapiao", null)]
    [InlineData("fapiao", "")]
    [InlineData("fapiao", " ")]
    public void ScoreReturnsZeroForBlankInputs(string? query, string? candidate)
    {
        Assert.Equal(0, PinyinMatcher.Score(query!, candidate!));
    }
}

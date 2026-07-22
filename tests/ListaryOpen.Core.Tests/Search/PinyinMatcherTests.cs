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
    [InlineData("xingkong", "星空.jpg")]
    [InlineData("xk", "星空.jpg")]
    [InlineData("zvezda", "звезда.txt")]
    public void ScoreMatchesCompleteChineseAndMultilingualTransliteration(string query, string candidate)
    {
        Assert.True(PinyinMatcher.Score(query, candidate) > 0);
    }

    [Fact]
    public void CreateSearchTextIncludesKnownPinyinAndInitials()
    {
        var searchText = PinyinMatcher.CreateSearchText("合同.docx");

        Assert.Contains("合同.docx", searchText);
        Assert.Contains("hetong.docx", searchText);
        Assert.Contains("ht.docx", searchText);
    }

    [Fact]
    public void CreateSearchAliasesOmitsOriginalTextAndAsciiDuplicates()
    {
        var aliases = PinyinMatcher.CreateSearchAliases("合同.docx");

        Assert.DoesNotContain("合同", aliases);
        Assert.Contains("hetong.docx", aliases);
        Assert.Contains("ht.docx", aliases);
        Assert.Equal(string.Empty, PinyinMatcher.CreateSearchAliases("invoice.docx"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void CreateSearchTextReturnsEmptyForBlankInput(string? value)
    {
        Assert.Equal(string.Empty, PinyinMatcher.CreateSearchText(value));
        Assert.Equal(string.Empty, PinyinMatcher.CreateSearchAliases(value));
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

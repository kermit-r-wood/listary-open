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
}

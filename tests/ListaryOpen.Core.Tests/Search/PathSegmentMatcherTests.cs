using ListaryOpen.Core.Search;

namespace ListaryOpen.Core.Tests.Search;

public sealed class PathSegmentMatcherTests
{
    [Theory]
    [InlineData(@"C:\Projects\中大成赢\门房图纸.cad", @"Projects\中大", true)]
    [InlineData(@"C:\Projects\中大成赢\门房图纸.cad", @"Projects/中大", true)]
    [InlineData(@"C:\Projects\中大成赢\门房图纸.cad", @"中大\门房", true)]
    [InlineData(@"C:\Projects\中大成赢\门房图纸.cad", @"门房\Projects", false)]
    [InlineData(@"C:\Other\readme.txt", @"Projects\中大", false)]
    [InlineData(@"C:\Projects\foo\bar.txt", "Projects", true)]
    // Component-boundary: mid-token concatenation must not match.
    [InlineData(@"C:\ab\file.txt", @"a\b", false)]
    [InlineData(@"C:\source\application\x", @"src\app", false)]
    // Single-segment mid-token rejection (skeptic gap): path:app must not match application.
    [InlineData(@"C:\application\file.txt", "app", false)]
    [InlineData(@"C:\project\file.txt", "pro", false)]
    [InlineData(@"C:\application\file.txt", "application", true)]
    [InlineData(@"C:\project\file.txt", "project", true)]
    [InlineData(@"C:\app\file.txt", "app", true)]
    public void MatchesOrderedPathSegments(string path, string expression, bool expected)
    {
        Assert.Equal(expected, PathSegmentMatcher.Matches(path, expression));
    }

    [Fact]
    public void SingleSegmentRejectsAsciiMidTokenPrefix()
    {
        // Shipped Matches() path only — no early Contains short-circuit.
        Assert.False(PathSegmentMatcher.Matches(@"C:\application\", "app"));
        Assert.False(PathSegmentMatcher.Matches(@"C:\project\docs", "pro"));
        Assert.False(PathSegmentMatcher.Matches(@"C:\Projects\application\x", "app"));
        Assert.True(PathSegmentMatcher.Matches(@"C:\app\docs", "app"));
        Assert.True(PathSegmentMatcher.Matches(@"C:\project\docs", "project"));
    }

    [Fact]
    public void MultiSegmentSeparatorAlignedSnippetStillWorks()
    {
        Assert.True(PathSegmentMatcher.Matches(
            @"C:\Projects\foo\bar.txt",
            @"Projects\foo"));
        // English mid-token multi-segment must not match via raw contains.
        Assert.False(PathSegmentMatcher.Matches(
            @"C:\Projectsource\file.txt",
            @"Projects"));
    }

    [Fact]
    public void ScoreIsZeroWhenNoMatch()
    {
        Assert.Equal(0, PathSegmentMatcher.Score(@"C:\a\b", @"x\y"));
        Assert.Equal(0, PathSegmentMatcher.Score(@"C:\application\x", "app"));
    }

    [Fact]
    public void ScoreIncreasesWithMoreSegments()
    {
        var single = PathSegmentMatcher.Score(@"C:\Projects\中大成赢\file.txt", "Projects");
        var multi = PathSegmentMatcher.Score(@"C:\Projects\中大成赢\file.txt", @"Projects\中大");
        Assert.True(multi > single);
    }
}

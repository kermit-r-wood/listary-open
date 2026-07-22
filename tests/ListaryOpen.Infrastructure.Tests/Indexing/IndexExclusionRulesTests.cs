using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class IndexExclusionRulesTests
{
    [Theory]
    [InlineData(".git")]
    [InlineData(".svn")]
    [InlineData(".hg")]
    [InlineData("node_modules")]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData(".vs")]
    [InlineData(".idea")]
    [InlineData(".vscode")]
    [InlineData("packages")]
    [InlineData("dist")]
    [InlineData("build")]
    [InlineData(".cache")]
    [InlineData("__pycache__")]
    [InlineData(".pytest_cache")]
    [InlineData(".next")]
    [InlineData(".nuxt")]
    [InlineData("target")]
    public void DefaultRulesDoNotSilentlyExcludeDirectoryNames(string directoryName)
    {
        Assert.False(IndexExclusionRules.Default.ShouldExcludeDirectoryName(directoryName));
    }

    [Fact]
    public void DefaultRulesDoNotExcludeOrdinaryDirectoryName()
    {
        Assert.False(IndexExclusionRules.Default.ShouldExcludeDirectoryName("Documents"));
    }

    [Fact]
    public void ShouldExcludeDirectoryPathUsesLastPathSegment()
    {
        var rules = new IndexExclusionRules(["node_modules"]);

        Assert.True(rules.ShouldExcludeDirectoryPath(Path.Combine("C:\\Projects", "node_modules")));
    }
}

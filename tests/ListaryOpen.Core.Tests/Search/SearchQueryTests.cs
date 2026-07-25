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

    [Fact]
    public void ParsedQueryReadsExtensionPathPhraseAndExclusions()
    {
        var query = new SearchQuery("ext:pdf path:src \"search panel\" !archive", SearchMode.FilesAndFolders);

        Assert.Equal(new[] { "search panel" }, query.Parsed.Phrases);
        Assert.Equal(new[] { "src" }, query.Parsed.PathTerms);
        Assert.Equal(new[] { "pdf" }, query.Parsed.Extensions);
        Assert.Equal(new[] { "archive" }, query.Parsed.ExcludedTerms);
        Assert.Equal("search panel", query.NormalizedText);
    }

    [Fact]
    public void ParsedQueryReadsMultipleExtensionOperatorsAsOrList()
    {
        var query = new SearchQuery("report ext:pdf ext:docx", SearchMode.FilesAndFolders);

        Assert.Equal(new[] { "report" }, query.Parsed.Terms);
        Assert.Equal(new[] { "pdf", "docx" }, query.Parsed.Extensions);
    }

    [Theory]
    [InlineData("ext:pdf,docx", new[] { "pdf", "docx" })]
    [InlineData("ext:pdf;docx", new[] { "pdf", "docx" })]
    [InlineData("ext:pdf|png", new[] { "pdf", "png" })]
    [InlineData("ext:.Pdf,.DOCX", new[] { "pdf", "docx" })]
    public void ParsedQuerySplitsCommaSeparatedExtensions(string text, string[] expected)
    {
        var query = new SearchQuery(text, SearchMode.FilesAndFolders);

        Assert.Equal(expected, query.Parsed.Extensions);
    }

    [Fact]
    public void ParsedQueryReadsFolderFilter()
    {
        var query = new SearchQuery("folder: report", SearchMode.FilesAndFolders);

        Assert.Equal(SearchMode.FoldersOnly, query.EffectiveMode);
        Assert.Equal(new[] { "report" }, query.Parsed.Terms);
    }

    [Fact]
    public void ParsedQueryReadsFileFilter()
    {
        var query = new SearchQuery("file: report", SearchMode.FoldersOnly);

        Assert.True(query.Parsed.FileOnly);
        Assert.Equal(SearchMode.FilesAndFolders, query.EffectiveMode);
        Assert.Equal(new[] { "report" }, query.Parsed.Terms);
    }

    [Fact]
    public void ParsedQueryReadsAppFilter()
    {
        var query = new SearchQuery("app: code", SearchMode.FilesAndFolders);

        Assert.True(query.Parsed.ApplicationsOnly);
        Assert.True(query.Parsed.FileOnly);
        Assert.Equal(new[] { "code" }, query.Parsed.Terms);
    }

    [Theory]
    [InlineData("ext:pdf")]
    [InlineData("path:src")]
    [InlineData("!ext:tmp")]
    public void ParsedFilterOnlyQueryHasNoRankingText(string text)
    {
        var query = new SearchQuery(text, SearchMode.FilesAndFolders);

        Assert.Equal(string.Empty, query.NormalizedText);
    }

    [Fact]
    public void ParsedQueryTreatsBareFileAndFolderAsSearchTerms()
    {
        var query = new SearchQuery("folder file", SearchMode.FilesAndFolders);

        Assert.Null(query.Parsed.ModeOverride);
        Assert.False(query.Parsed.FileOnly);
        Assert.Equal(new[] { "folder", "file" }, query.Parsed.Terms);
    }

    [Fact]
    public void ParsedQueryTreatsUnclosedQuoteAsTerm()
    {
        var query = new SearchQuery("foo \"bar baz", SearchMode.FilesAndFolders);

        Assert.Equal(new[] { "foo", "bar baz" }, query.Parsed.Terms);
        Assert.Empty(query.Parsed.Phrases);
    }
}

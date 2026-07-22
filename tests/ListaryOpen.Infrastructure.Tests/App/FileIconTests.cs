using ListaryOpen.App;

namespace ListaryOpen.Infrastructure.Tests.App;

public sealed class FileIconTests
{
    [Theory]
    [InlineData("C:\\Docs\\Report.PDF", false, "extension:.pdf")]
    [InlineData("C:\\Docs\\Notes.txt", false, "extension:.txt")]
    [InlineData("C:\\Docs\\README", false, "extension:")]
    [InlineData("C:\\Docs\\Folder", true, "folder")]
    public void CommonFileTypesShareStableCacheKeys(string path, bool isDirectory, string expected)
    {
        Assert.Equal(expected, FileIcon.GetCacheKey(path, isDirectory));
    }

    [Theory]
    [InlineData("C:\\Apps\\First.exe", "C:\\Apps\\Second.exe")]
    [InlineData("C:\\Links\\First.lnk", "C:\\Links\\Second.lnk")]
    [InlineData("C:\\Icons\\First.ico", "C:\\Icons\\Second.ico")]
    public void FilesWithEmbeddedOrCustomIconsUsePathSpecificCacheKeys(string first, string second)
    {
        Assert.NotEqual(FileIcon.GetCacheKey(first, false), FileIcon.GetCacheKey(second, false));
    }
}

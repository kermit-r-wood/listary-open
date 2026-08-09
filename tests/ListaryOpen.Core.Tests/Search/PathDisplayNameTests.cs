using ListaryOpen.Core.Search;

namespace ListaryOpen.Core.Tests.Search;

public sealed class PathDisplayNameTests
{
    [Fact]
    public void FromFullPath_DecodesPercentEncodedPathBasenameToLeaf()
    {
        var encoded = @"C:\Users\paulx\.grok\sessions\C%3A%5CUsers%5Cpaulx%5COneDrive%5Cprojects%5Clistary_open";
        Assert.Equal("listary_open", PathDisplayName.FromFullPath(encoded));
    }

    [Fact]
    public void FromFullPath_LeavesNormalNamesUnchanged()
    {
        Assert.Equal("listary_open", PathDisplayName.FromFullPath(@"C:\Users\paulx\OneDrive\projects\listary_open"));
        Assert.Equal("readme.txt", PathDisplayName.FromFullPath(@"C:\Docs\readme.txt"));
    }

    [Fact]
    public void FromFullPath_StripsShortcutExtension()
    {
        Assert.Equal("Notepad", PathDisplayName.FromFullPath(@"C:\Shortcuts\Notepad.lnk"));
    }

    [Fact]
    public void LooksLikePercentEncodedPath_DetectsDriveAndSeparatorEncodings()
    {
        Assert.True(PathDisplayName.LooksLikePercentEncodedPath(
            "C%3A%5CUsers%5Cpaulx%5COneDrive%5Cprojects%5Clistary_open"));
        Assert.False(PathDisplayName.LooksLikePercentEncodedPath("listary_open"));
        Assert.False(PathDisplayName.LooksLikePercentEncodedPath("file%20name.txt"));
    }
}

using ListaryOpen.Core.Search;

namespace ListaryOpen.Core.Tests.Search;

public sealed class ApplicationPathTests
{
    [Theory]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Visual Studio Code.lnk", false, true)]
    [InlineData(@"C:\Users\user\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\App.lnk", false, true)]
    [InlineData(@"C:\Program Files\App\app.exe", false, true)]
    [InlineData(@"C:\Program Files (x86)\App\app.exe", false, true)]
    [InlineData(@"C:\Docs\report.pdf", false, false)]
    [InlineData(@"C:\Docs\tool.exe", false, false)]
    [InlineData(@"C:\Program Files\App", true, false)]
    public void ClassifiesApplicationPaths(string path, bool isDirectory, bool expected)
    {
        Assert.Equal(expected, ApplicationPath.IsApplication(path, isDirectory));
    }
}

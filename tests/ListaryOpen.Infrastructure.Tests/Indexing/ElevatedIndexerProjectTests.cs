using System.Xml.Linq;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class ElevatedIndexerProjectTests
{
    [Fact]
    public void AppProjectBuildsAndCopiesNativeHooksWhenEnabled()
    {
        var projectPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ListaryOpen.App",
            "ListaryOpen.App.csproj");

        var document = XDocument.Load(projectPath);
        var projectXml = document.ToString(SaveOptions.DisableFormatting);
        var target = document.Descendants("Target").Single(element => (string?)element.Attribute("Name") == "BuildNativeHooks");
        var targetXml = target.ToString(SaveOptions.DisableFormatting);

        Assert.Contains("<BuildNativeHooks", projectXml, StringComparison.Ordinal);
        Assert.Contains("x86_64-pc-windows-gnullvm", targetXml, StringComparison.Ordinal);
        Assert.Contains("i686-pc-windows-gnullvm", targetXml, StringComparison.Ordinal);
        Assert.Contains("cargo build --locked", targetXml, StringComparison.Ordinal);
        Assert.Contains("-p listary_open_hook_host", targetXml, StringComparison.Ordinal);
        Assert.Contains("-p listary_open_hook", targetXml, StringComparison.Ordinal);
        Assert.Contains(@"hooks\x64\ListaryOpen.HookHost.exe", targetXml, StringComparison.Ordinal);
        Assert.Contains(@"hooks\x64\ListaryOpen.Hook.dll", targetXml, StringComparison.Ordinal);
        Assert.Contains(@"hooks\x86\ListaryOpen.HookHost.exe", targetXml, StringComparison.Ordinal);
        Assert.Contains(@"hooks\x86\ListaryOpen.Hook.dll", targetXml, StringComparison.Ordinal);
        Assert.Contains("libunwind.dll", targetXml, StringComparison.Ordinal);
        Assert.Contains("Error", targetXml, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ListaryOpen.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root.");
    }
}

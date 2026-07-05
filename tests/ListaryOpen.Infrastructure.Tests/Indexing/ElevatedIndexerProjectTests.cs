using System.Diagnostics;
using System.Xml.Linq;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class ElevatedIndexerProjectTests
{
    [Fact]
    public void ElevatedIndexerProjectBuildsAsWindowsExecutable()
    {
        var projectPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ListaryOpen.Indexer.Elevated",
            "ListaryOpen.Indexer.Elevated.csproj");

        var document = XDocument.Load(projectPath);
        var outputType = document.Descendants("OutputType").Single().Value;

        Assert.Equal("WinExe", outputType);
    }

    [Fact]
    public void AppBuildOutputContainsCompleteElevatedIndexerBundle()
    {
        var appOutputDirectory = FindAppBuildOutputDirectory();

        Assert.True(
            File.Exists(Path.Combine(appOutputDirectory, "ListaryOpen.Indexer.Elevated.exe")),
            "The App output directory must include the elevated indexer executable.");
        Assert.True(
            File.Exists(Path.Combine(appOutputDirectory, "ListaryOpen.Indexer.Elevated.dll")),
            "The App output directory must include the elevated indexer assembly used by the executable host.");
        Assert.True(
            File.Exists(Path.Combine(appOutputDirectory, "ListaryOpen.Indexer.Elevated.deps.json")),
            "The App output directory must include the elevated indexer dependency manifest.");
        Assert.True(
            File.Exists(Path.Combine(appOutputDirectory, "ListaryOpen.Indexer.Elevated.runtimeconfig.json")),
            "The App output directory must include the elevated indexer runtime config.");
    }

    [Fact]
    public void AppBuildOutputElevatedIndexerStartsFromCopiedBundle()
    {
        var helperPath = Path.Combine(FindAppBuildOutputDirectory(), "ListaryOpen.Indexer.Elevated.exe");
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        });
        Assert.NotNull(process);

        if (!process.WaitForExit(milliseconds: 5000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The copied elevated indexer helper did not exit within the smoke test timeout.");
        }

        var stderr = process.StandardError.ReadToEnd();
        Assert.Equal(2, process.ExitCode);
        Assert.Contains("Usage: ListaryOpen.Indexer.Elevated", stderr);
    }

    [Fact]
    public void AppProjectCopiesElevatedIndexerBundleFromResolvedTargetPath()
    {
        var projectPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ListaryOpen.App",
            "ListaryOpen.App.csproj");

        var document = XDocument.Load(projectPath);
        var target = document.Descendants("Target").Single(element => (string?)element.Attribute("Name") == "CopyElevatedIndexerBundle");

        Assert.Contains(
            target.Descendants("ElevatedIndexerProjectPath"),
            element => element.Value.Contains("ListaryOpen.Indexer.Elevated.csproj", StringComparison.Ordinal));
        Assert.Contains(
            target.Descendants("MSBuild"),
            element =>
                string.Equals((string?)element.Attribute("Projects"), "$(ElevatedIndexerProjectPath)", StringComparison.Ordinal) &&
                string.Equals((string?)element.Attribute("Targets"), "GetTargetPath", StringComparison.Ordinal));
        Assert.DoesNotContain("bin\\$(Configuration)\\$(TargetFramework)", target.ToString(SaveOptions.DisableFormatting));
        Assert.Contains("**\\*", target.ToString(SaveOptions.DisableFormatting), StringComparison.Ordinal);
    }

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

    private static string FindAppBuildOutputDirectory()
    {
        var repositoryRoot = FindRepositoryRoot();
        var outputDirectory = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        var targetFramework = outputDirectory.Name;
        string? runtimeIdentifier = null;
        var configurationDirectory = outputDirectory.Parent;

        if (!targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            runtimeIdentifier = targetFramework;
            outputDirectory = configurationDirectory ?? throw new DirectoryNotFoundException("Could not find target framework output directory.");
            targetFramework = outputDirectory.Name;
            configurationDirectory = outputDirectory.Parent;
        }

        var configuration = configurationDirectory?.Name;
        if (string.IsNullOrWhiteSpace(configuration))
        {
            throw new DirectoryNotFoundException("Could not infer build configuration from test output directory.");
        }

        var path = Path.Combine(
            repositoryRoot,
            "src",
            "ListaryOpen.App",
            "bin",
            configuration,
            targetFramework);

        return runtimeIdentifier is null ? path : Path.Combine(path, runtimeIdentifier);
    }
}

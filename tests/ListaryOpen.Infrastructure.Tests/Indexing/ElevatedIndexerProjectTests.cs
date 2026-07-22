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
        using var appBuild = BuildAppProjectIntoTemporaryOutput();
        var appOutputDirectory = appBuild.OutputDirectory;

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
        using var appBuild = BuildAppProjectIntoTemporaryOutput();
        var helperPath = Path.Combine(appBuild.OutputDirectory, "ListaryOpen.Indexer.Elevated.exe");
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
        var publishTarget = document.Descendants("Target").Single(element =>
            (string?)element.Attribute("Name") == "CopyNativeHooksToPublishDirectory");
        var publishTargetXml = publishTarget.ToString(SaveOptions.DisableFormatting);

        Assert.DoesNotContain(@"C:\Users\paulx", projectXml, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$(SCOOP)", projectXml, StringComparison.Ordinal);
        Assert.Contains(@"$(USERPROFILE)\scoop", projectXml, StringComparison.Ordinal);
        Assert.Contains(@"mingw-mstorsjo-llvm-msvcrt\current", projectXml, StringComparison.Ordinal);
        Assert.Contains(
            document.Descendants("NativeHooksRoot"),
            element => ((string?)element.Attribute("Condition"))?.Contains("'$(NativeHooksRoot)' == ''", StringComparison.Ordinal) == true);
        Assert.Contains(
            document.Descendants("NativeHooksX64Target"),
            element => ((string?)element.Attribute("Condition"))?.Contains("'$(NativeHooksX64Target)' == ''", StringComparison.Ordinal) == true);
        Assert.Contains(
            document.Descendants("NativeHooksX86Target"),
            element => ((string?)element.Attribute("Condition"))?.Contains("'$(NativeHooksX86Target)' == ''", StringComparison.Ordinal) == true);
        Assert.Contains(
            document.Descendants("NativeHooksX64RuntimeDll"),
            element => ((string?)element.Attribute("Condition"))?.Contains("'$(NativeHooksX64RuntimeDll)' == ''", StringComparison.Ordinal) == true);
        Assert.Contains(
            document.Descendants("NativeHooksX86RuntimeDll"),
            element => ((string?)element.Attribute("Condition"))?.Contains("'$(NativeHooksX86RuntimeDll)' == ''", StringComparison.Ordinal) == true);
        Assert.Contains("<BuildNativeHooks", projectXml, StringComparison.Ordinal);
        Assert.Contains("x86_64-pc-windows-gnullvm", projectXml, StringComparison.Ordinal);
        Assert.Contains("i686-pc-windows-gnullvm", projectXml, StringComparison.Ordinal);
        Assert.Contains("cargo build --release --locked", targetXml, StringComparison.Ordinal);
        Assert.Contains(@"\release\listary_open_hook_host.exe", targetXml, StringComparison.Ordinal);
        Assert.Contains("-p listary_open_hook_host", targetXml, StringComparison.Ordinal);
        Assert.Contains("-p listary_open_hook", targetXml, StringComparison.Ordinal);
        Assert.Contains("--target $(NativeHooksX64Target)", targetXml, StringComparison.Ordinal);
        Assert.Contains("--target $(NativeHooksX86Target)", targetXml, StringComparison.Ordinal);
        Assert.Contains(@"hooks\x64\ListaryOpen.HookHost.exe", targetXml, StringComparison.Ordinal);
        Assert.Contains(@"hooks\x64\ListaryOpen.Hook.dll", targetXml, StringComparison.Ordinal);
        Assert.Contains(@"hooks\x86\ListaryOpen.HookHost.exe", targetXml, StringComparison.Ordinal);
        Assert.Contains(@"hooks\x86\ListaryOpen.Hook.dll", targetXml, StringComparison.Ordinal);
        Assert.Contains("libunwind.dll", targetXml, StringComparison.Ordinal);
        Assert.Contains("Error", targetXml, StringComparison.Ordinal);
        Assert.Equal("Publish", (string?)publishTarget.Attribute("AfterTargets"));
        Assert.Contains("$(OutDir)hooks\\**\\*", publishTargetXml, StringComparison.Ordinal);
        Assert.Contains("$(PublishDir)hooks\\", publishTargetXml, StringComparison.Ordinal);
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

    private static AppBuildOutput BuildAppProjectIntoTemporaryOutput()
    {
        var outputRoot = Directory.CreateTempSubdirectory("listary-open-app-build-");
        var baseOutputPath = outputRoot.FullName + Path.DirectorySeparatorChar;
        var projectPath = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "ListaryOpen.App",
            "ListaryOpen.App.csproj");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--no-restore");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.ArgumentList.Add("--disable-build-servers");
        startInfo.ArgumentList.Add("-nr:false");
        startInfo.ArgumentList.Add("-p:BuildNativeHooks=false");
        startInfo.ArgumentList.Add("-p:BaseOutputPath=" + baseOutputPath);
        startInfo.ArgumentList.Add("-p:UseSharedCompilation=false");

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(milliseconds: 120_000))
        {
            process.Kill(entireProcessTree: true);
            outputRoot.Delete(recursive: true);
            throw new TimeoutException("The temporary app build did not exit within the test timeout.");
        }

        if (!Task.WaitAll([stdoutTask, stderrTask], millisecondsTimeout: 10_000))
        {
            outputRoot.Delete(recursive: true);
            throw new TimeoutException("The temporary app build output pipes did not close within the test timeout.");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            outputRoot.Delete(recursive: true);
            throw new InvalidOperationException(
                $"Temporary app build failed with exit code {process.ExitCode}.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
        }

        var outputDirectory = Path.Combine(outputRoot.FullName, "Debug", "net8.0-windows");
        if (!Directory.Exists(outputDirectory))
        {
            outputRoot.Delete(recursive: true);
            throw new DirectoryNotFoundException($"Temporary app build output was not found: {outputDirectory}");
        }

        return new AppBuildOutput(outputRoot, outputDirectory);
    }

    private sealed class AppBuildOutput : IDisposable
    {
        private readonly DirectoryInfo _outputRoot;

        public AppBuildOutput(DirectoryInfo outputRoot, string outputDirectory)
        {
            _outputRoot = outputRoot;
            OutputDirectory = outputDirectory;
        }

        public string OutputDirectory { get; }

        public void Dispose()
        {
            try
            {
                _outputRoot.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

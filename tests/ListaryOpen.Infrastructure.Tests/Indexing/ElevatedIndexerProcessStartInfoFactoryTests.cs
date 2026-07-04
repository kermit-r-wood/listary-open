using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class ElevatedIndexerProcessStartInfoFactoryTests
{
    [Fact]
    public void CreateRedirectedUsesStdoutAndStderrRedirection()
    {
        var startInfo = ElevatedIndexerProcessStartInfoFactory.CreateRedirected(
            "C:\\Tools\\ListaryOpen.Indexer.Elevated.exe",
            "C:\\Users\\paulx");

        Assert.Equal("C:\\Tools\\ListaryOpen.Indexer.Elevated.exe", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(new[] { "scan", "C:\\Users\\paulx" }, startInfo.ArgumentList);
    }

    [Fact]
    public void CreateUacFileUsesRunAsAndScanToFileArguments()
    {
        var startInfo = ElevatedIndexerProcessStartInfoFactory.CreateUacFile(
            "C:\\Tools\\ListaryOpen.Indexer.Elevated.exe",
            "C:\\Users\\paulx",
            "C:\\Temp\\records.jsonl",
            "C:\\Temp\\error.txt");

        Assert.Equal("C:\\Tools\\ListaryOpen.Indexer.Elevated.exe", startInfo.FileName);
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal("runas", startInfo.Verb);
        Assert.False(startInfo.RedirectStandardOutput);
        Assert.False(startInfo.RedirectStandardError);
        Assert.Equal(
            new[] { "scan-to-file", "C:\\Users\\paulx", "C:\\Temp\\records.jsonl", "C:\\Temp\\error.txt" },
            startInfo.ArgumentList);
    }
}

using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class ElevatedIndexerProcessStartInfoFactoryTests
{
    [Fact]
    public void CreateRedirectedFileUsesScanToFileWithoutRunAs()
    {
        var startInfo = ElevatedIndexerProcessStartInfoFactory.CreateRedirectedFile(
            "C:\\Tools\\ListaryOpen.Indexer.Elevated.exe",
            "C:\\Users\\paulx",
            "C:\\Temp\\records.jsonl",
            "C:\\Temp\\error.txt");

        Assert.Equal("C:\\Tools\\ListaryOpen.Indexer.Elevated.exe", startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(string.Empty, startInfo.Verb);
        Assert.False(startInfo.RedirectStandardOutput);
        Assert.False(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(
            new[] { "scan-to-file", "C:\\Users\\paulx", "C:\\Temp\\records.jsonl", "C:\\Temp\\error.txt" },
            startInfo.ArgumentList);
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

    [Fact]
    public void CreateRedirectedJournalStateFileUsesJournalStateCommand()
    {
        var startInfo = ElevatedIndexerProcessStartInfoFactory.CreateRedirectedJournalStateFile(
            "C:\\Tools\\ListaryOpen.Indexer.Elevated.exe",
            "C:\\Users\\paulx",
            "C:\\Temp\\state.jsonl",
            "C:\\Temp\\error.txt");

        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(
            new[] { "journal-state-to-file", "C:\\Users\\paulx", "C:\\Temp\\state.jsonl", "C:\\Temp\\error.txt" },
            startInfo.ArgumentList);
    }

    [Fact]
    public void CreateRedirectedJournalChangesFileUsesReadJournalCommand()
    {
        var startInfo = ElevatedIndexerProcessStartInfoFactory.CreateRedirectedJournalChangesFile(
            "C:\\Tools\\ListaryOpen.Indexer.Elevated.exe",
            "C:\\Users\\paulx",
            expectedUsnJournalId: 9,
            startUsn: 100,
            endUsn: 200,
            "C:\\Temp\\changes.jsonl",
            "C:\\Temp\\error.txt");

        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(
            new[] { "read-journal-to-file", "C:\\Users\\paulx", "9", "100", "200", "C:\\Temp\\changes.jsonl", "C:\\Temp\\error.txt" },
            startInfo.ArgumentList);
    }
}

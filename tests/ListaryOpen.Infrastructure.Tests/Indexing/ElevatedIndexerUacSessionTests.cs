using System.Diagnostics;
using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class ElevatedIndexerUacSessionTests
{
    [Fact]
    public async Task StickyUacSessionStartsWorkerOnlyOnceForMultipleCommands()
    {
        var helperPath = ResolveBuiltHelperPath();
        Assert.True(
            File.Exists(helperPath),
            $"Built elevated helper not found at '{helperPath}'. Build ListaryOpen.Indexer.Elevated before this test.");

        var helperDirectory = Path.GetDirectoryName(helperPath)!;
        var tmp = Path.Combine(helperDirectory, "data", "tmp");
        Directory.CreateDirectory(tmp);

        var startCount = 0;
        using var session = new ElevatedIndexerUacSession(
            helperPath,
            (_, pipeName) =>
            {
                Interlocked.Increment(ref startCount);
                // No runas: unit tests stay non-interactive. Worker still speaks the pipe protocol.
                var startInfo = new ProcessStartInfo
                {
                    FileName = helperPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = helperDirectory
                };
                startInfo.ArgumentList.Add("worker");
                startInfo.ArgumentList.Add("--pipe");
                startInfo.ArgumentList.Add(pipeName);
                return startInfo;
            });

        // Without elevation, journal open fails (exit 5) after the worker has started.
        // Two sequential commands must still reuse the single worker process.
        var firstError = await Assert.ThrowsAsync<ElevatedIndexerException>(() =>
            session.RunCommandAsync(
                "journal-state-to-file",
                @"C:\",
                Path.Combine(tmp, "listary-open-indexer-" + Guid.NewGuid().ToString("N") + ".jsonl"),
                Path.Combine(tmp, "listary-open-indexer-" + Guid.NewGuid().ToString("N") + ".err"),
                expectedUsnJournalId: 0,
                startUsn: 0,
                endUsn: 0,
                CancellationToken.None));

        var secondError = await Assert.ThrowsAsync<ElevatedIndexerException>(() =>
            session.RunCommandAsync(
                "journal-state-to-file",
                @"C:\",
                Path.Combine(tmp, "listary-open-indexer-" + Guid.NewGuid().ToString("N") + ".jsonl"),
                Path.Combine(tmp, "listary-open-indexer-" + Guid.NewGuid().ToString("N") + ".err"),
                expectedUsnJournalId: 0,
                startUsn: 0,
                endUsn: 0,
                CancellationToken.None));

        Assert.Equal(1, startCount);
        Assert.Contains("exited with code", firstError.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exited with code", secondError.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveBuiltHelperPath()
    {
        var package = Environment.GetEnvironmentVariable("LISTARYOPEN_PACKAGE_DIR");
        if (!string.IsNullOrWhiteSpace(package))
        {
            var packaged = Path.Combine(package, "ListaryOpen.Indexer.Elevated.exe");
            if (File.Exists(packaged))
            {
                return packaged;
            }
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            foreach (var relative in new[]
                     {
                         Path.Combine("src", "ListaryOpen.Indexer.Elevated", "bin", "Release", "net8.0-windows", "win-x64", "ListaryOpen.Indexer.Elevated.exe"),
                         Path.Combine("src", "ListaryOpen.Indexer.Elevated", "bin", "Release", "net8.0-windows", "ListaryOpen.Indexer.Elevated.exe"),
                         Path.Combine("src", "ListaryOpen.Indexer.Elevated", "bin", "Debug", "net8.0-windows", "ListaryOpen.Indexer.Elevated.exe"),
                         Path.Combine("artifacts", "ListaryOpen-fixed", "ListaryOpen.Indexer.Elevated.exe"),
                         Path.Combine("artifacts", "ListaryOpen", "ListaryOpen.Indexer.Elevated.exe")
                     })
            {
                var candidate = Path.Combine(directory.FullName, relative);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            directory = directory.Parent;
        }

        return Path.Combine(
            AppContext.BaseDirectory,
            "ListaryOpen.Indexer.Elevated.exe");
    }
}

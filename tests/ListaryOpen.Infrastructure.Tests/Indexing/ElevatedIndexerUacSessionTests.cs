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

        // An invalid relative volume root fails deterministically whether the test
        // runner is elevated or not. Journal-state, journal-read, and scan commands
        // must still share the single long-lived worker process (one UAC entry).
        const string invalidVolumeRoot = "not-a-volume-root";
        var commands = new[]
        {
            "journal-state-to-file",
            "read-journal-to-file",
            "scan-to-file",
            "journal-state-to-file"
        };

        var errors = new List<ElevatedIndexerException>();
        foreach (var command in commands)
        {
            var extension = command == "scan-to-file" ? ".bin" : ".jsonl";
            var error = await Assert.ThrowsAsync<ElevatedIndexerException>(() =>
                session.RunCommandAsync(
                    command,
                    invalidVolumeRoot,
                    Path.Combine(tmp, "listary-open-indexer-" + Guid.NewGuid().ToString("N") + extension),
                    Path.Combine(tmp, "listary-open-indexer-" + Guid.NewGuid().ToString("N") + ".err"),
                    expectedUsnJournalId: 0,
                    startUsn: 0,
                    endUsn: 0,
                    CancellationToken.None));
            errors.Add(error);
        }

        Assert.Equal(1, startCount);
        Assert.Equal(commands.Length, errors.Count);
        Assert.All(
            errors,
            error => Assert.Contains("exited with code", error.Message, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StickyUacSessionRestartsWorkerOnlyAfterProcessExit()
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

        // First multi-command sequence shares one worker; an explicit kill forces
        // recovery elevation (exactly one replacement start).
        const string invalidVolumeRoot = "not-a-volume-root";
        await Assert.ThrowsAsync<ElevatedIndexerException>(() =>
            session.RunCommandAsync(
                "journal-state-to-file",
                invalidVolumeRoot,
                Path.Combine(tmp, "listary-open-indexer-" + Guid.NewGuid().ToString("N") + ".jsonl"),
                Path.Combine(tmp, "listary-open-indexer-" + Guid.NewGuid().ToString("N") + ".err"),
                expectedUsnJournalId: 0,
                startUsn: 0,
                endUsn: 0,
                CancellationToken.None));
        await Assert.ThrowsAsync<ElevatedIndexerException>(() =>
            session.RunCommandAsync(
                "read-journal-to-file",
                invalidVolumeRoot,
                Path.Combine(tmp, "listary-open-indexer-" + Guid.NewGuid().ToString("N") + ".jsonl"),
                Path.Combine(tmp, "listary-open-indexer-" + Guid.NewGuid().ToString("N") + ".err"),
                expectedUsnJournalId: 0,
                startUsn: 0,
                endUsn: 0,
                CancellationToken.None));

        Assert.Equal(1, startCount);

        session.KillWorkerForTests();

        await Assert.ThrowsAsync<ElevatedIndexerException>(() =>
            session.RunCommandAsync(
                "scan-to-file",
                invalidVolumeRoot,
                Path.Combine(tmp, "listary-open-indexer-" + Guid.NewGuid().ToString("N") + ".bin"),
                Path.Combine(tmp, "listary-open-indexer-" + Guid.NewGuid().ToString("N") + ".err"),
                expectedUsnJournalId: 0,
                startUsn: 0,
                endUsn: 0,
                CancellationToken.None));

        Assert.Equal(2, startCount);
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

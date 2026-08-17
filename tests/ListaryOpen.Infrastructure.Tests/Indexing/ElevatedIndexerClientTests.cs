using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Core.Search;
using ListaryOpen.Indexer.Elevated;
using ListaryOpen.Infrastructure.Indexing;
using ListaryOpen.Infrastructure.Indexing.Ntfs;
using ListaryOpen.Infrastructure.Search;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class ElevatedIndexerClientTests
{
    [Fact]
    public void ExplicitHelperUsesItsOwnTrustedTempDirectory()
    {
        var helperPath = Path.Combine("C:\\Published", "ListaryOpen.Indexer.Elevated.exe");

        var tempDirectory = ElevatedIndexerClient.ResolveHelperTempDirectory(helperPath);

        Assert.Equal(Path.Combine("C:\\Published", "data", "tmp"), tempDirectory);
    }

    private static readonly SemaphoreSlim TraceGate = new(1, 1);

    [Fact]
    public void IsAvailableReturnsFalseWhenExplicitHelperPathIsMissing()
    {
        var helperPath = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid(), "missing.exe");

        var client = new ElevatedIndexerClient(helperPath);

        Assert.False(client.IsAvailable);
    }

    [Fact]
    public void IsAvailableReturnsFalseWhenHelperExecutableExistsWithoutRequiredSidecarFiles()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = Path.Combine(tempDirectory, "ListaryOpen.Indexer.Elevated.exe");
            File.WriteAllText(helperPath, "placeholder");

            var client = new ElevatedIndexerClient(helperPath, () => true);

            Assert.False(client.IsAvailable);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void ResolveDefaultHelperPathSkipsIncompleteBesideAppHelperAndUsesRepositoryBuildOutput()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        var appOutputDirectory = Path.Combine(
            repositoryRoot,
            "src",
            "ListaryOpen.App",
            "bin",
            "Debug",
            "net8.0-windows");
        var helperOutputDirectory = Path.Combine(
            repositoryRoot,
            "src",
            "ListaryOpen.Indexer.Elevated",
            "bin",
            "Debug",
            "net8.0-windows");
        Directory.CreateDirectory(appOutputDirectory);
        Directory.CreateDirectory(helperOutputDirectory);

        try
        {
            File.WriteAllText(Path.Combine(appOutputDirectory, "ListaryOpen.Indexer.Elevated.exe"), "incomplete");
            var expectedHelperPath = CreateUsableHelperBundle(helperOutputDirectory);

            var resolved = ElevatedIndexerClient.ResolveDefaultHelperPath(appOutputDirectory);

            Assert.Equal(expectedHelperPath, resolved);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveDefaultHelperPathSkipsIncompleteBesideAppHelperAndUsesRuntimeIdentifierRepositoryBuildOutput()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        var appOutputDirectory = Path.Combine(
            repositoryRoot,
            "src",
            "ListaryOpen.App",
            "bin",
            "Release",
            "net8.0-windows",
            "win-x64");
        var helperOutputDirectory = Path.Combine(
            repositoryRoot,
            "src",
            "ListaryOpen.Indexer.Elevated",
            "bin",
            "Release",
            "net8.0-windows",
            "win-x64");
        Directory.CreateDirectory(appOutputDirectory);
        Directory.CreateDirectory(helperOutputDirectory);

        try
        {
            File.WriteAllText(Path.Combine(appOutputDirectory, "ListaryOpen.Indexer.Elevated.exe"), "incomplete");
            var expectedHelperPath = CreateUsableHelperBundle(helperOutputDirectory);

            var resolved = ElevatedIndexerClient.ResolveDefaultHelperPath(appOutputDirectory);

            Assert.Equal(expectedHelperPath, resolved);
        }
        finally
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public void IsAvailableReturnsTrueWhenExplicitHelperPathExistsAndProcessIsElevated()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = CreateUsableHelperBundle(tempDirectory);

            var client = new ElevatedIndexerClient(helperPath, () => true);

            Assert.True(client.IsAvailable);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void IsAvailableReturnsFalseWhenExplicitHelperPathExistsButProcessIsNotElevated()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = CreateUsableHelperBundle(tempDirectory);

            var client = new ElevatedIndexerClient(helperPath, () => false);

            Assert.False(client.IsAvailable);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void IsAvailableReturnsTrueWhenExplicitHelperPathExistsAndUacElevationIsEnabled()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = CreateUsableHelperBundle(tempDirectory);
            var client = new ElevatedIndexerClient(helperPath, () => false);

            client.EnableUacElevation();

            Assert.True(client.IsAvailable);
            Assert.True(client.UacElevationEnabled);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ScanNtfsAsyncReturnsEmptyRecordsAndWarnsWhenHelperIsMissing()
    {
        var helperPath = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid(), "missing.exe");
        var client = new ElevatedIndexerClient(helperPath);
        await using var traceCapture = await TraceCapture.StartAsync();

        var records = await CollectAsync(client.ScanNtfsAsync(new IndexRoot(Path.GetTempPath()), CancellationToken.None));
        Trace.Flush();

        Assert.Empty(records);
        Assert.Contains("Elevated indexer helper is not available", traceCapture.Messages);
    }

    [Fact]
    public async Task ScanNtfsAsyncReturnsEmptyRecordsWithoutStartingHelperWhenProcessIsNotElevated()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = CreateUsableHelperBundle(tempDirectory);
            var processCreated = false;
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _, _, _) =>
                {
                    processCreated = true;
                    throw new InvalidOperationException("Helper process should not be created.");
                },
                () => false);

            var records = await CollectAsync(client.ScanNtfsAsync(new IndexRoot(Path.GetTempPath()), CancellationToken.None));

            Assert.Empty(records);
            Assert.False(processCreated);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ScanNtfsAsyncReadsRecordsFromUacElevatedFileHelperWhenProcessIsNotElevated()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = CreateUsableHelperBundle(tempDirectory);
            var indexerTempDirectory = Path.Combine(tempDirectory, "data", "tmp");
            ElevatedFileHelperRequest? createdRequest = null;
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _, _, _) => throw new InvalidOperationException("Redirected helper should not be created."),
                () => false,
                () => true,
                (path, root, outputPath, errorPath) =>
                {
                    createdRequest = new ElevatedFileHelperRequest(path, root, outputPath, errorPath);
                    return new FileWritingElevatedIndexerProcess(
                        outputPath,
                        errorPath,
                        EncodeBinaryRecords(FileRecord.Create(
                            @"C:\Docs\Elevated.txt",
                            false,
                            15,
                            new DateTimeOffset(2026, 7, 4, 0, 0, 0, TimeSpan.Zero))),
                        error: string.Empty,
                        exitCode: 0);
                },
                indexerTempDirectory);

            var records = await CollectAsync(client.ScanNtfsAsync(new IndexRoot("C:\\Docs"), CancellationToken.None));

            var record = Assert.Single(records);
            Assert.Equal("C:\\Docs\\Elevated.txt", record.FullPath);
            Assert.False(record.IsDirectory);
            Assert.Equal(15, record.SizeBytes);
            Assert.Equal(new DateTimeOffset(2026, 7, 4, 0, 0, 0, TimeSpan.Zero), record.LastWriteTime);
            Assert.NotNull(createdRequest);
            Assert.Equal(helperPath, createdRequest!.HelperPath);
            Assert.Equal("C:\\Docs", createdRequest.RootPath);
            Assert.Equal(indexerTempDirectory, Path.GetDirectoryName(createdRequest.OutputPath));
            Assert.Equal(indexerTempDirectory, Path.GetDirectoryName(createdRequest.ErrorPath));
            Assert.False(File.Exists(createdRequest.OutputPath));
            Assert.False(File.Exists(createdRequest.ErrorPath));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ScanNtfsAsyncThrowsLaunchCanceledWhenUacElevationIsCanceled()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = CreateUsableHelperBundle(tempDirectory);
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _, _, _) => throw new InvalidOperationException("Redirected helper should not be created."),
                () => false,
                () => true,
                (_, _, _, _) => new StartThrowingElevatedIndexerProcess(new Win32Exception(1223, "The operation was canceled by the user.")));

            var exception = await Assert.ThrowsAsync<ElevatedIndexerLaunchCanceledException>(
                () => CollectAsync(client.ScanNtfsAsync(new IndexRoot("C:\\Docs"), CancellationToken.None)));

            Assert.Contains("canceled", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ScanNtfsAsyncThrowsLaunchCanceledWhenUacElevationStartReturnsFalse()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = CreateUsableHelperBundle(tempDirectory);
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _, _, _) => throw new InvalidOperationException("Redirected helper should not be created."),
                () => false,
                () => true,
                (_, _, _, _) => new StartFalseElevatedIndexerProcess());

            var exception = await Assert.ThrowsAsync<ElevatedIndexerLaunchCanceledException>(
                () => CollectAsync(client.ScanNtfsAsync(new IndexRoot("C:\\Docs"), CancellationToken.None)));

            Assert.Contains("canceled", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ScanNtfsAsyncThrowsWhenHelperCannotStart()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = CreateUsableHelperBundle(tempDirectory, "not a portable executable");
            var client = new ElevatedIndexerClient(helperPath, () => true);
            await using var traceCapture = await TraceCapture.StartAsync();

            var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => CollectAsync(client.ScanNtfsAsync(new IndexRoot(Path.GetTempPath()), CancellationToken.None)));

            Assert.Contains("Failed to start elevated indexer helper", exception.Message);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ScanNtfsAsyncDoesNotWrapStartFalseFailureTwice()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = CreateUsableHelperBundle(tempDirectory);
            var client = new ElevatedIndexerClient(helperPath, (_, _, _, _) => new StartFalseElevatedIndexerProcess());

            var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => CollectAsync(client.ScanNtfsAsync(new IndexRoot("C:\\Docs"), CancellationToken.None)));

            Assert.Equal($"Failed to start elevated indexer helper '{helperPath}'.", exception.Message);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ScanNtfsAsyncThrowsWhenHelperExitsNonZero()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = CreateUsableHelperBundle(tempDirectory);
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _, outputPath, errorPath) => new FileWritingElevatedIndexerProcess(
                    outputPath,
                    errorPath,
                    output: string.Empty,
                    error: "Access denied.",
                    exitCode: 5));

            var exception = await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => CollectAsync(client.ScanNtfsAsync(new IndexRoot("C:\\Docs"), CancellationToken.None)));

            Assert.Contains("exited with code 5", exception.Message);
            Assert.Contains("Access denied.", exception.Message);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ScanNtfsAsyncParsesBinaryRecordsFromHelperOutput()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = CreateUsableHelperBundle(tempDirectory);
            var output = EncodeBinaryRecords(
                FileRecord.Create(
                    @"C:\Docs\Invoice.xlsx",
                    false,
                    12,
                    new DateTimeOffset(2026, 7, 3, 0, 0, 0, TimeSpan.Zero)),
                FileRecord.Create(
                    @"C:\Docs\Projects",
                    true,
                    0,
                    new DateTimeOffset(2026, 7, 3, 0, 1, 0, TimeSpan.Zero)));
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _, outputPath, errorPath) => new FileWritingElevatedIndexerProcess(
                    outputPath,
                    errorPath,
                    output,
                    error: string.Empty,
                    exitCode: 0));

            var records = await CollectAsync(client.ScanNtfsAsync(new IndexRoot("C:\\Docs"), CancellationToken.None));

            Assert.Collection(
                records,
                record =>
                {
                    Assert.Equal("C:\\Docs\\Invoice.xlsx", record.FullPath);
                    Assert.False(record.IsDirectory);
                    Assert.Equal(12, record.SizeBytes);
                    Assert.Equal(new DateTimeOffset(2026, 7, 3, 0, 0, 0, TimeSpan.Zero), record.LastWriteTime);
                },
                record =>
                {
                    Assert.Equal("C:\\Docs\\Projects", record.FullPath);
                    Assert.True(record.IsDirectory);
                    Assert.Equal(0, record.SizeBytes);
                    Assert.Equal(new DateTimeOffset(2026, 7, 3, 0, 1, 0, TimeSpan.Zero), record.LastWriteTime);
                });
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ScanNtfsAsyncParsesManyBinaryFramesAcrossTailBufferBoundaries()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            // Enough records that total payload exceeds the 256 KiB tailer buffer.
            var recordsIn = new List<FileRecord>(8_000);
            for (var i = 0; i < 8_000; i++)
            {
                recordsIn.Add(FileRecord.Create(
                    $@"C:\Docs\folder-medium-name\f-{i:D5}-项目-file.txt",
                    false,
                    i,
                    new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero).AddSeconds(i)));
            }

            var output = EncodeBinaryRecords(recordsIn.ToArray());
            Assert.True(output.Length > 256 * 1024, $"payload was only {output.Length} bytes");
            var helperPath = CreateUsableHelperBundle(tempDirectory);
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _, outputPath, errorPath) => new FileWritingElevatedIndexerProcess(
                    outputPath,
                    errorPath,
                    output,
                    error: string.Empty,
                    exitCode: 0));

            var records = await CollectAsync(
                client.ScanNtfsAsync(new IndexRoot("C:\\Docs"), CancellationToken.None));

            Assert.Equal(8_000, records.Count);
            Assert.Equal(@"C:\Docs\folder-medium-name\f-00000-项目-file.txt", records[0].FullPath);
            Assert.Equal(@"C:\Docs\folder-medium-name\f-07999-项目-file.txt", records[^1].FullPath);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task QueryJournalStateAsyncParsesJsonStateFromHelperOutput()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = CreateUsableHelperBundle(tempDirectory);
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _, outputPath, errorPath) => new FileWritingElevatedIndexerProcess(
                    outputPath,
                    errorPath,
                    output: "{\"usnJournalId\":9,\"lowestValidUsn\":50,\"nextUsn\":200}",
                    error: string.Empty,
                    exitCode: 0));

            var state = await client.QueryJournalStateAsync(new IndexRoot("C:\\Docs"), CancellationToken.None);

            Assert.NotNull(state);
            Assert.Equal(9ul, state!.UsnJournalId);
            Assert.Equal(50, state.LowestValidUsn);
            Assert.Equal(200, state.NextUsn);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task QueryJournalStateAsyncRejectsMissingSuccessfulHelperOutput()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = CreateUsableHelperBundle(tempDirectory);
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _, _, _) => new NoOutputElevatedIndexerProcess(exitCode: 0));

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => client.QueryJournalStateAsync(new IndexRoot("C:\\Docs"), CancellationToken.None));

            Assert.Contains("missing", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ScanNtfsAsyncYieldsFlushedRecordsBeforeHelperExits()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = CreateUsableHelperBundle(tempDirectory);
            StreamingFileWritingElevatedIndexerProcess? helperProcess = null;
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _, outputPath, errorPath) =>
                {
                    helperProcess = new StreamingFileWritingElevatedIndexerProcess(outputPath, errorPath);
                    return helperProcess;
                });

            await using var enumerator = client
                .ScanNtfsAsync(new IndexRoot("C:\\Docs"), CancellationToken.None)
                .GetAsyncEnumerator();
            var firstMove = enumerator.MoveNextAsync().AsTask();

            await helperProcess!.FirstRecordWritten.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await firstMove.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("C:\\Docs\\First.txt", enumerator.Current.FullPath);
            Assert.False(helperProcess.HasExited);

            helperProcess.AllowExit();
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("C:\\Docs\\Second.txt", enumerator.Current.FullPath);
            Assert.False(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ReadJournalChangesAsyncParsesJsonChangesFromHelperOutput()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = CreateUsableHelperBundle(tempDirectory);
            var output = string.Join(
                Environment.NewLine,
                "{\"kind\":\"delete\",\"fullPath\":\"C:\\\\Docs\\\\OldName.txt\"}",
                "{\"kind\":\"upsert\",\"fullPath\":\"C:\\\\Docs\\\\NewName.txt\",\"isDirectory\":false,\"sizeBytes\":42,\"lastWriteTime\":\"2026-07-09T00:00:00+00:00\",\"fileReferenceNumber\":\"99\"}",
                "{\"kind\":\"directoryRenameOrMove\"}");
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _, outputPath, errorPath) => new FileWritingElevatedIndexerProcess(
                    outputPath,
                    errorPath,
                    output,
                    error: string.Empty,
                    exitCode: 0));

            var changes = new List<UsnJournalChange>();
            await foreach (var change in client.ReadJournalChangesAsync(new IndexRoot("C:\\Docs"), 9, 100, 200, CancellationToken.None))
            {
                changes.Add(change);
            }

            Assert.Collection(
                changes,
                change =>
                {
                    Assert.Equal(UsnJournalChangeKind.Delete, change.Kind);
                    Assert.Equal("C:\\Docs\\OldName.txt", change.FullPath);
                },
                change =>
                {
                    Assert.Equal(UsnJournalChangeKind.Upsert, change.Kind);
                    Assert.Equal("C:\\Docs\\NewName.txt", change.Record!.FullPath);
                    Assert.Equal(42, change.Record.SizeBytes);
                    Assert.Equal(99ul, change.Record.FileReferenceNumber);
                },
                change => Assert.Equal(UsnJournalChangeKind.DirectoryRenameOrMove, change.Kind));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteJournalThenParseThenApplyRemovesFrnZeroHardLinkGhost()
    {
        // Elevated writer → client parse → index apply on the real incremental hard-link path:
        // FRN=0 USN upserts, then HARD_LINK_CHANGE resync+delete JSONL.
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);
        var dbPath = Path.Combine(tempDirectory, "index.db");
        var trustedTemp = ElevatedIndexerOutputPathValidator.GetTrustedTempDirectory();
        Directory.CreateDirectory(trustedTemp);
        var journalPath = Path.Combine(
            trustedTemp,
            "listary-open-indexer-journal-rt-" + Guid.NewGuid() + ".jsonl");

        try
        {
            const ulong frn = 88;
            var now = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

            await using var index = await SqliteSearchIndex.OpenAsync(dbPath, CancellationToken.None);
            await index.UpsertManyAsync(
                new[]
                {
                    FileRecord.Create(@"C:\Docs\a.txt", false, 10, now, fileReferenceNumber: 0),
                    FileRecord.Create(@"C:\Docs\a1.txt", false, 10, now, fileReferenceNumber: 0),
                    FileRecord.Create(@"C:\Docs\other.txt", false, 10, now, fileReferenceNumber: 0)
                },
                CancellationToken.None);

            var produced = new UsnJournalChange[]
            {
                UsnJournalChange.HardLinkResync(
                    frn,
                    new[] { FileRecord.Create(@"C:\Docs\a1.txt", false, 10, now, frn) }),
                UsnJournalChange.Delete(@"C:\Docs\a.txt")
            };

            await ElevatedIndexerRecordWriter.WriteJournalChangesFileAsync(
                EnumerateJournal(produced),
                journalPath,
                CancellationToken.None);

            var written = await File.ReadAllTextAsync(journalPath);
            Assert.Contains("\"fileReferenceNumber\":\"88\"", written, StringComparison.Ordinal);
            Assert.Contains("\"kind\":\"hardLinkResync\"", written, StringComparison.Ordinal);

            var helperPath = CreateUsableHelperBundle(tempDirectory);
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _, outputPath, errorPath) => new FileWritingElevatedIndexerProcess(
                    outputPath,
                    errorPath,
                    written,
                    error: string.Empty,
                    exitCode: 0));

            var parsed = new List<UsnJournalChange>();
            await foreach (var change in client.ReadJournalChangesAsync(
                               new IndexRoot("C:\\Docs"),
                               9,
                               100,
                               200,
                               CancellationToken.None))
            {
                parsed.Add(change);
            }

            Assert.Equal(2, parsed.Count);
            Assert.Equal(UsnJournalChangeKind.HardLinkResync, parsed[0].Kind);
            Assert.Equal(frn, parsed[0].FileReferenceNumber);
            Assert.Equal(frn, Assert.Single(parsed[0].LiveHardLinkRecords!).FileReferenceNumber);
            Assert.Equal(UsnJournalChangeKind.Delete, parsed[1].Kind);
            Assert.Equal(@"C:\Docs\a.txt", parsed[1].FullPath);

            var checkpoint = new UsnJournalCheckpoint(
                VolumeRoot: @"C:\",
                FileSystemName: "NTFS",
                UsnJournalId: 1,
                NextUsn: 300,
                RulesVersion: 1,
                LastFullScanAt: now);

            var apply = await UsnJournalChangeApplier.ApplyAsync(
                index,
                parsed,
                checkpoint,
                CancellationToken.None);
            Assert.False(apply.RequiresFullRescan);

            var results = await index.SearchAsync(
                new SearchQuery("a", SearchMode.FilesAndFolders),
                CancellationToken.None);
            Assert.Contains(
                results,
                item => item.Record.FullPath.Equals(@"C:\Docs\a1.txt", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                results,
                item => item.Record.FullPath.Equals(@"C:\Docs\a.txt", StringComparison.OrdinalIgnoreCase));

            // FRN stamped: empty resync purges remaining live name by file_reference.
            await UsnJournalChangeApplier.ApplyAsync(
                index,
                new[] { UsnJournalChange.HardLinkResync(frn, Array.Empty<FileRecord>()) },
                checkpoint with { NextUsn = 301 },
                CancellationToken.None);
            var after = await index.SearchAsync(
                new SearchQuery("a1", SearchMode.FilesAndFolders),
                CancellationToken.None);
            Assert.DoesNotContain(
                after,
                item => item.Record.FullPath.Equals(@"C:\Docs\a1.txt", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try
            {
                if (File.Exists(journalPath))
                {
                    File.Delete(journalPath);
                }
            }
            catch
            {
                // ignore
            }

            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static async IAsyncEnumerable<UsnJournalChange> EnumerateJournal(params UsnJournalChange[] changes)
    {
        foreach (var change in changes)
        {
            await Task.Yield();
            yield return change;
        }
    }

    [Fact]
    public async Task ReadJournalChangesAsyncThrowsWhenHelperIsUnavailable()
    {
        var client = new ElevatedIndexerClient("C:\\Missing\\ListaryOpen.Indexer.Elevated.exe");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CollectChangesAsync(client.ReadJournalChangesAsync(new IndexRoot("C:\\Docs"), 9, 100, 200, CancellationToken.None)));
    }

    [Fact]
    public async Task ReadJournalChangesAsyncThrowsWhenHelperDoesNotWriteChangesFile()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = CreateUsableHelperBundle(tempDirectory);
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _, _, _) => new NoOutputElevatedIndexerProcess(exitCode: 0));

            await Assert.ThrowsAsync<InvalidDataException>(
                () => CollectChangesAsync(client.ReadJournalChangesAsync(new IndexRoot("C:\\Docs"), 9, 100, 200, CancellationToken.None)));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ScanNtfsAsyncThrowsInvalidDataExceptionWhenHelperOutputIsMalformed()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = CreateUsableHelperBundle(tempDirectory);
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _, outputPath, errorPath) => new FileWritingElevatedIndexerProcess(
                    outputPath,
                    errorPath,
                    outputBytes: Encoding.UTF8.GetBytes("not-a-binary-frame"),
                    error: string.Empty,
                    exitCode: 0));

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => CollectAsync(client.ScanNtfsAsync(new IndexRoot("C:\\Docs"), CancellationToken.None)));
            Assert.Contains("corrupt", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ScanNtfsAsyncThrowsInvalidDataExceptionWhenBinaryFrameIsTruncated()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = CreateUsableHelperBundle(tempDirectory);
            var full = EncodeBinaryRecords(FileRecord.Create(
                @"C:\Docs\Invoice.xlsx",
                false,
                12,
                new DateTimeOffset(2026, 7, 3, 0, 0, 0, TimeSpan.Zero)));
            var truncated = full.AsSpan(0, full.Length / 2).ToArray();
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _, outputPath, errorPath) => new FileWritingElevatedIndexerProcess(
                    outputPath,
                    errorPath,
                    outputBytes: truncated,
                    error: string.Empty,
                    exitCode: 0));

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => CollectAsync(client.ScanNtfsAsync(new IndexRoot("C:\\Docs"), CancellationToken.None)));

            Assert.Contains("incomplete", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ScanNtfsAsyncThrowsCancellationAfterWaitingForKilledHelperAndObservingReads()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = CreateUsableHelperBundle(tempDirectory);
            var helperProcess = new RecordingElevatedIndexerProcess();
            var client = new ElevatedIndexerClient(helperPath, (_, _, _, _) => helperProcess);
            using var cancellation = new CancellationTokenSource();

            var scanTask = Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in client.ScanNtfsAsync(new IndexRoot(Path.GetTempPath()), cancellation.Token))
                {
                }
            });

            await helperProcess.WaitForCancelableWaitAsync();
            await cancellation.CancelAsync();
            await scanTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(helperProcess.KillCalled);
            Assert.Equal(2, helperProcess.WaitForExitTokens.Count);
            Assert.True(helperProcess.WaitForExitTokens[0].CanBeCanceled);
            Assert.True(helperProcess.WaitForExitTokens[1].CanBeCanceled);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static async Task<List<FileRecord>> CollectAsync(IAsyncEnumerable<FileRecord> records)
    {
        var collected = new List<FileRecord>();
        await foreach (var record in records)
        {
            collected.Add(record);
        }

        return collected;
    }

    private static async Task<List<UsnJournalChange>> CollectChangesAsync(IAsyncEnumerable<UsnJournalChange> changes)
    {
        var collected = new List<UsnJournalChange>();
        await foreach (var change in changes)
        {
            collected.Add(change);
        }

        return collected;
    }

    private static string CreateUsableHelperBundle(string directory, string executableContent = "placeholder")
    {
        var helperPath = Path.Combine(directory, "ListaryOpen.Indexer.Elevated.exe");
        File.WriteAllText(helperPath, executableContent);
        File.WriteAllText(Path.Combine(directory, "ListaryOpen.Indexer.Elevated.dll"), "placeholder");
        File.WriteAllText(Path.Combine(directory, "ListaryOpen.Indexer.Elevated.deps.json"), "{}");
        File.WriteAllText(Path.Combine(directory, "ListaryOpen.Indexer.Elevated.runtimeconfig.json"), "{}");
        return helperPath;
    }

    private sealed record ElevatedFileHelperRequest(
        string HelperPath,
        string RootPath,
        string OutputPath,
        string ErrorPath);

    private sealed class CompletedElevatedIndexerProcess : IElevatedIndexerProcess
    {
        private readonly string _stdout;
        private readonly string _stderr;

        public CompletedElevatedIndexerProcess(string stdout, string stderr, int exitCode)
        {
            _stdout = stdout;
            _stderr = stderr;
            ExitCode = exitCode;
        }

        public int ExitCode { get; }

        public bool HasExited => true;

        public bool Start() => true;

        public Task<string> ReadStandardOutputToEndAsync(CancellationToken cancellationToken)
            => Task.FromResult(_stdout);

        public Task<string> ReadStandardErrorToEndAsync(CancellationToken cancellationToken)
            => Task.FromResult(_stderr);

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void Kill()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class StartFalseElevatedIndexerProcess : IElevatedIndexerProcess
    {
        public int ExitCode => -1;

        public bool HasExited => true;

        public bool Start() => false;

        public Task<string> ReadStandardOutputToEndAsync(CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);

        public Task<string> ReadStandardErrorToEndAsync(CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void Kill()
        {
        }

        public void Dispose()
        {
        }
    }

    private static byte[] EncodeBinaryRecords(params FileRecord[] records)
    {
        using var stream = new MemoryStream();
        foreach (var record in records)
        {
            var frame = new byte[ElevatedIndexerBinaryCodec.GetFrameSize(record.FullPath)];
            Assert.True(ElevatedIndexerBinaryCodec.TryWriteFrame(frame, record, out _));
            stream.Write(frame);
        }

        return stream.ToArray();
    }

    private sealed class FileWritingElevatedIndexerProcess : IElevatedIndexerProcess
    {
        private readonly string _outputPath;
        private readonly string _errorPath;
        private readonly byte[] _outputBytes;
        private readonly string _error;

        public FileWritingElevatedIndexerProcess(
            string outputPath,
            string errorPath,
            string output,
            string error,
            int exitCode)
            : this(outputPath, errorPath, Encoding.UTF8.GetBytes(output), error, exitCode)
        {
        }

        public FileWritingElevatedIndexerProcess(
            string outputPath,
            string errorPath,
            byte[] outputBytes,
            string error,
            int exitCode)
        {
            _outputPath = outputPath;
            _errorPath = errorPath;
            _outputBytes = outputBytes;
            _error = error;
            ExitCode = exitCode;
        }

        public int ExitCode { get; }

        public bool HasExited => true;

        public bool Start()
        {
            File.WriteAllBytes(_outputPath, _outputBytes);
            File.WriteAllText(_errorPath, _error);
            return true;
        }

        public Task<string> ReadStandardOutputToEndAsync(CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);

        public Task<string> ReadStandardErrorToEndAsync(CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void Kill()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class StreamingFileWritingElevatedIndexerProcess : IElevatedIndexerProcess
    {
        private static readonly byte[] FirstRecord = EncodeBinaryRecords(
            FileRecord.Create(
                @"C:\Docs\First.txt",
                false,
                1,
                new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero)));
        private static readonly byte[] SecondRecord = EncodeBinaryRecords(
            FileRecord.Create(
                @"C:\Docs\Second.txt",
                false,
                2,
                new DateTimeOffset(2026, 7, 15, 0, 0, 1, TimeSpan.Zero)));

        private readonly string _outputPath;
        private readonly string _errorPath;
        private readonly TaskCompletionSource _allowExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstRecordWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _runTask = Task.CompletedTask;

        public StreamingFileWritingElevatedIndexerProcess(string outputPath, string errorPath)
        {
            _outputPath = outputPath;
            _errorPath = errorPath;
        }

        public Task FirstRecordWritten => _firstRecordWritten.Task;

        public int ExitCode => 0;

        public bool HasExited { get; private set; }

        public bool Start()
        {
            _runTask = RunAsync();
            return true;
        }

        public Task<string> ReadStandardErrorToEndAsync(CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => _runTask.WaitAsync(cancellationToken);

        public void AllowExit() => _allowExit.TrySetResult();

        public void Kill() => _allowExit.TrySetResult();

        public void Dispose()
        {
        }

        private async Task RunAsync()
        {
            try
            {
                await using var stream = new FileStream(
                    _outputPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);
                await stream.WriteAsync(FirstRecord);
                await stream.FlushAsync();
                _firstRecordWritten.TrySetResult();
                await _allowExit.Task;
                await stream.WriteAsync(SecondRecord);
                await stream.FlushAsync();
                await File.WriteAllTextAsync(_errorPath, string.Empty);
            }
            finally
            {
                HasExited = true;
            }
        }
    }

    private sealed class NoOutputElevatedIndexerProcess : IElevatedIndexerProcess
    {
        public NoOutputElevatedIndexerProcess(int exitCode)
        {
            ExitCode = exitCode;
        }

        public int ExitCode { get; }

        public bool HasExited => true;

        public bool Start() => true;

        public Task<string> ReadStandardOutputToEndAsync(CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);

        public Task<string> ReadStandardErrorToEndAsync(CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void Kill()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class StartThrowingElevatedIndexerProcess : IElevatedIndexerProcess
    {
        private readonly Exception _exception;

        public StartThrowingElevatedIndexerProcess(Exception exception)
        {
            _exception = exception;
        }

        public int ExitCode => -1;

        public bool HasExited => true;

        public bool Start() => throw _exception;

        public Task<string> ReadStandardOutputToEndAsync(CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);

        public Task<string> ReadStandardErrorToEndAsync(CancellationToken cancellationToken)
            => Task.FromResult(string.Empty);

        public Task WaitForExitAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public void Kill()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingElevatedIndexerProcess : IElevatedIndexerProcess
    {
        private readonly TaskCompletionSource _cancelableWaitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool KillCalled { get; private set; }

        public bool OutputReadObserved { get; private set; }

        public bool ErrorReadObserved { get; private set; }

        public List<CancellationToken> WaitForExitTokens { get; } = new();

        public int ExitCode => 0;

        public bool HasExited { get; private set; }

        public bool Start() => true;

        public Task<string> ReadStandardOutputToEndAsync(CancellationToken cancellationToken)
            => ReadUntilCanceledAsync(cancellationToken, () => OutputReadObserved = true);

        public Task<string> ReadStandardErrorToEndAsync(CancellationToken cancellationToken)
            => ReadUntilCanceledAsync(cancellationToken, () => ErrorReadObserved = true);

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            WaitForExitTokens.Add(cancellationToken);
            if (!cancellationToken.CanBeCanceled)
            {
                HasExited = true;
                return Task.CompletedTask;
            }

            _cancelableWaitStarted.SetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task WaitForCancelableWaitAsync() => _cancelableWaitStarted.Task;

        public void Kill()
        {
            KillCalled = true;
        }

        public void Dispose()
        {
        }

        private static async Task<string> ReadUntilCanceledAsync(CancellationToken cancellationToken, Action onObserved)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return string.Empty;
            }
            finally
            {
                onObserved();
            }
        }
    }

    private sealed class TraceCapture : IAsyncDisposable
    {
        private readonly RecordingTraceListener _listener;

        private TraceCapture(RecordingTraceListener listener)
        {
            _listener = listener;
        }

        public string Messages => _listener.Messages;

        public static async Task<TraceCapture> StartAsync()
        {
            await TraceGate.WaitAsync();
            var listener = new RecordingTraceListener();
            Trace.Listeners.Add(listener);
            return new TraceCapture(listener);
        }

        public ValueTask DisposeAsync()
        {
            Trace.Listeners.Remove(_listener);
            _listener.Dispose();
            TraceGate.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingTraceListener : TraceListener
    {
        private readonly StringBuilder _messages = new();

        public string Messages => _messages.ToString();

        public override void Write(string? message)
        {
            _messages.Append(message);
        }

        public override void WriteLine(string? message)
        {
            _messages.AppendLine(message);
        }
    }
}

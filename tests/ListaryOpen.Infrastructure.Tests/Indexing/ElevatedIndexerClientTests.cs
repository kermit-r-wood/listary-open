using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Indexing;
using ListaryOpen.Infrastructure.Indexing.Ntfs;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class ElevatedIndexerClientTests
{
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
                        "{\"fullPath\":\"C:\\\\Docs\\\\Elevated.txt\",\"isDirectory\":false,\"sizeBytes\":15,\"lastWriteTime\":\"2026-07-04T00:00:00+00:00\"}",
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
    public async Task ScanNtfsAsyncParsesJsonRecordsFromHelperOutput()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = CreateUsableHelperBundle(tempDirectory);
            var output = string.Join(
                Environment.NewLine,
                "{\"fullPath\":\"C:\\\\Docs\\\\Invoice.xlsx\",\"isDirectory\":false,\"sizeBytes\":12,\"lastWriteTime\":\"2026-07-03T00:00:00+00:00\"}",
                "{\"fullPath\":\"C:\\\\Docs\\\\Projects\",\"isDirectory\":true,\"sizeBytes\":0,\"lastWriteTime\":\"2026-07-03T00:01:00+00:00\"}");
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
                "{\"kind\":\"upsert\",\"fullPath\":\"C:\\\\Docs\\\\NewName.txt\",\"isDirectory\":false,\"sizeBytes\":42,\"lastWriteTime\":\"2026-07-09T00:00:00+00:00\"}",
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
            await foreach (var change in client.ReadJournalChangesAsync(new IndexRoot("C:\\Docs"), 100, 200, CancellationToken.None))
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
                },
                change => Assert.Equal(UsnJournalChangeKind.DirectoryRenameOrMove, change.Kind));
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
                    output: "{not-json}",
                    error: string.Empty,
                    exitCode: 0));

            await Assert.ThrowsAsync<InvalidDataException>(
                () => CollectAsync(client.ScanNtfsAsync(new IndexRoot("C:\\Docs"), CancellationToken.None)));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{\"fullPath\":\"C:\\\\Docs\\\\Invoice.xlsx\",\"sizeBytes\":12,\"lastWriteTime\":\"2026-07-03T00:00:00+00:00\"}")]
    [InlineData("{\"fullPath\":\"C:\\\\Docs\\\\Invoice.xlsx\",\"isDirectory\":false,\"lastWriteTime\":\"2026-07-03T00:00:00+00:00\"}")]
    [InlineData("{\"fullPath\":\"C:\\\\Docs\\\\Invoice.xlsx\",\"isDirectory\":false,\"sizeBytes\":12}")]
    public async Task ScanNtfsAsyncThrowsInvalidDataExceptionWithLineNumberWhenRequiredPrimitiveFieldIsMissing(
        string stdout)
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
                    output: stdout,
                    error: string.Empty,
                    exitCode: 0));

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => CollectAsync(client.ScanNtfsAsync(new IndexRoot("C:\\Docs"), CancellationToken.None)));

            Assert.Contains("line 1", exception.Message);
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
            Assert.False(helperProcess.WaitForExitTokens[1].CanBeCanceled);
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

    private sealed class FileWritingElevatedIndexerProcess : IElevatedIndexerProcess
    {
        private readonly string _outputPath;
        private readonly string _errorPath;
        private readonly string _output;
        private readonly string _error;

        public FileWritingElevatedIndexerProcess(
            string outputPath,
            string errorPath,
            string output,
            string error,
            int exitCode)
        {
            _outputPath = outputPath;
            _errorPath = errorPath;
            _output = output;
            _error = error;
            ExitCode = exitCode;
        }

        public int ExitCode { get; }

        public bool HasExited => true;

        public bool Start()
        {
            File.WriteAllText(_outputPath, _output);
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

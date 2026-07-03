using System.Diagnostics;
using System.Text;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Indexing;

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
    public void IsAvailableReturnsTrueWhenExplicitHelperPathExists()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = Path.Combine(tempDirectory, "ListaryOpen.Indexer.Elevated.exe");
            File.WriteAllText(helperPath, "placeholder");

            var client = new ElevatedIndexerClient(helperPath);

            Assert.True(client.IsAvailable);
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
    public async Task ScanNtfsAsyncThrowsWhenHelperCannotStart()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = Path.Combine(tempDirectory, "ListaryOpen.Indexer.Elevated.exe");
            File.WriteAllText(helperPath, "not a portable executable");
            var client = new ElevatedIndexerClient(helperPath);
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
            var helperPath = Path.Combine(tempDirectory, "ListaryOpen.Indexer.Elevated.exe");
            File.WriteAllText(helperPath, "placeholder");
            var client = new ElevatedIndexerClient(helperPath, (_, _) => new StartFalseElevatedIndexerProcess());

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
            var helperPath = Path.Combine(tempDirectory, "ListaryOpen.Indexer.Elevated.exe");
            File.WriteAllText(helperPath, "placeholder");
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _) => new CompletedElevatedIndexerProcess(stdout: string.Empty, stderr: "Access denied.", exitCode: 5));

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
            var helperPath = Path.Combine(tempDirectory, "ListaryOpen.Indexer.Elevated.exe");
            File.WriteAllText(helperPath, "placeholder");
            var stdout = string.Join(
                Environment.NewLine,
                "{\"fullPath\":\"C:\\\\Docs\\\\Invoice.xlsx\",\"isDirectory\":false,\"sizeBytes\":12,\"lastWriteTime\":\"2026-07-03T00:00:00+00:00\"}",
                "{\"fullPath\":\"C:\\\\Docs\\\\Projects\",\"isDirectory\":true,\"sizeBytes\":0,\"lastWriteTime\":\"2026-07-03T00:01:00+00:00\"}");
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _) => new CompletedElevatedIndexerProcess(stdout, stderr: string.Empty, exitCode: 0));

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
    public async Task ScanNtfsAsyncThrowsInvalidDataExceptionWhenHelperOutputIsMalformed()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = Path.Combine(tempDirectory, "ListaryOpen.Indexer.Elevated.exe");
            File.WriteAllText(helperPath, "placeholder");
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _) => new CompletedElevatedIndexerProcess("{not-json}", stderr: string.Empty, exitCode: 0));

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
            var helperPath = Path.Combine(tempDirectory, "ListaryOpen.Indexer.Elevated.exe");
            File.WriteAllText(helperPath, "placeholder");
            var client = new ElevatedIndexerClient(
                helperPath,
                (_, _) => new CompletedElevatedIndexerProcess(stdout, stderr: string.Empty, exitCode: 0));

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
            var helperPath = Path.Combine(tempDirectory, "ListaryOpen.Indexer.Elevated.exe");
            File.WriteAllText(helperPath, "placeholder");
            var helperProcess = new RecordingElevatedIndexerProcess();
            var client = new ElevatedIndexerClient(helperPath, (_, _) => helperProcess);
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
            Assert.True(helperProcess.OutputReadObserved);
            Assert.True(helperProcess.ErrorReadObserved);
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

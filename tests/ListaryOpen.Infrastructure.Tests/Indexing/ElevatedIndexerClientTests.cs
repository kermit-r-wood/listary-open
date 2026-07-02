using System.Diagnostics;
using System.Text;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Indexing;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class ElevatedIndexerClientTests
{
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
        using var listener = new RecordingTraceListener();

        Trace.Listeners.Add(listener);
        try
        {
            var records = await CollectAsync(client.ScanNtfsAsync(new IndexRoot(Path.GetTempPath()), CancellationToken.None));
            Trace.Flush();

            Assert.Empty(records);
            Assert.Contains("Elevated indexer helper is not available", listener.Messages);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    [Fact]
    public async Task ScanNtfsAsyncReturnsEmptyRecordsAndTracesWhenHelperCannotStart()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var helperPath = Path.Combine(tempDirectory, "ListaryOpen.Indexer.Elevated.exe");
            File.WriteAllText(helperPath, "not a portable executable");
            var client = new ElevatedIndexerClient(helperPath);
            using var listener = new RecordingTraceListener();

            Trace.Listeners.Add(listener);
            try
            {
                var records = await CollectAsync(client.ScanNtfsAsync(new IndexRoot(Path.GetTempPath()), CancellationToken.None));
                Trace.Flush();

                Assert.Empty(records);
                Assert.Contains("Failed to start elevated indexer helper", listener.Messages);
            }
            finally
            {
                Trace.Listeners.Remove(listener);
            }
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

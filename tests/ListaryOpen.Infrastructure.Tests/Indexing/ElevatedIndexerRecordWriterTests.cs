using ListaryOpen.Core.Indexing;
using ListaryOpen.Indexer.Elevated;
using ListaryOpen.Infrastructure.Indexing.Ntfs;
using System.Text;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class ElevatedIndexerRecordWriterTests
{
    [Fact]
    public async Task WriteFileAsyncWritesWebJsonLines()
    {
        var tempDirectory = ElevatedIndexerOutputPathValidator.GetTrustedTempDirectory();
        Directory.CreateDirectory(tempDirectory);
        var outputPath = Path.Combine(tempDirectory, "listary-open-indexer-" + Guid.NewGuid() + ".jsonl");

        try
        {
            var records = Enumerate(
                FileRecord.Create(
                    "C:\\Docs\\Report.txt",
                    isDirectory: false,
                    sizeBytes: 42,
                    new DateTimeOffset(2026, 7, 4, 1, 2, 3, TimeSpan.Zero)),
                FileRecord.Create(
                    "C:\\Docs\\Archive",
                    isDirectory: true,
                    sizeBytes: 0,
                    new DateTimeOffset(2026, 7, 4, 1, 3, 0, TimeSpan.Zero)));

            await ElevatedIndexerRecordWriter.WriteFileAsync(records, outputPath, CancellationToken.None);

            var lines = await File.ReadAllLinesAsync(outputPath);
            Assert.Equal(2, lines.Length);
            Assert.Equal(
                "{\"fullPath\":\"C:\\\\Docs\\\\Report.txt\",\"isDirectory\":false,\"sizeBytes\":42,\"lastWriteTime\":\"2026-07-04T01:02:03+00:00\",\"fileReferenceNumber\":\"0\"}",
                lines[0]);
            Assert.Equal(
                "{\"fullPath\":\"C:\\\\Docs\\\\Archive\",\"isDirectory\":true,\"sizeBytes\":0,\"lastWriteTime\":\"2026-07-04T01:03:00+00:00\",\"fileReferenceNumber\":\"0\"}",
                lines[1]);
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public async Task WriteJournalChangesFileAsyncStampsFileReferenceOnUpsertFileRenameAndHardLinkResync()
    {
        var tempDirectory = ElevatedIndexerOutputPathValidator.GetTrustedTempDirectory();
        Directory.CreateDirectory(tempDirectory);
        var outputPath = Path.Combine(tempDirectory, "listary-open-indexer-journal-" + Guid.NewGuid() + ".jsonl");

        try
        {
            var now = new DateTimeOffset(2026, 7, 4, 1, 2, 3, TimeSpan.Zero);
            var upsert = UsnJournalChange.Upsert(
                FileRecord.Create(@"C:\Docs\a.txt", false, 10, now, fileReferenceNumber: 42));
            var rename = UsnJournalChange.FileRename(
                @"C:\Docs\old.txt",
                FileRecord.Create(@"C:\Docs\new.txt", false, 11, now, fileReferenceNumber: 43));
            var resync = UsnJournalChange.HardLinkResync(
                77,
                new[]
                {
                    FileRecord.Create(@"C:\Docs\a1.txt", false, 10, now, fileReferenceNumber: 77)
                });

            await ElevatedIndexerRecordWriter.WriteJournalChangesFileAsync(
                EnumerateChanges(upsert, rename, resync),
                outputPath,
                CancellationToken.None);

            var lines = await File.ReadAllLinesAsync(outputPath);
            Assert.Equal(3, lines.Length);
            Assert.Contains("\"kind\":\"upsert\"", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"fileReferenceNumber\":\"42\"", lines[0], StringComparison.Ordinal);
            Assert.Contains("\"kind\":\"fileRename\"", lines[1], StringComparison.Ordinal);
            Assert.Contains("\"fileReferenceNumber\":\"43\"", lines[1], StringComparison.Ordinal);
            Assert.Contains("\"kind\":\"hardLinkResync\"", lines[2], StringComparison.Ordinal);
            Assert.Contains("\"fileReferenceNumber\":\"77\"", lines[2], StringComparison.Ordinal);
            Assert.Contains("\"fullPath\":\"C:\\\\Docs\\\\a1.txt\"", lines[2], StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    private static async IAsyncEnumerable<UsnJournalChange> EnumerateChanges(params UsnJournalChange[] changes)
    {
        foreach (var change in changes)
        {
            await Task.Yield();
            yield return change;
        }
    }

    [Fact]
    public async Task WriteFileAsyncDoesNotOverwriteExistingFile()
    {
        var tempDirectory = ElevatedIndexerOutputPathValidator.GetTrustedTempDirectory();
        Directory.CreateDirectory(tempDirectory);
        var outputPath = Path.Combine(tempDirectory, "listary-open-indexer-" + Guid.NewGuid() + ".jsonl");

        try
        {
            await File.WriteAllTextAsync(outputPath, "existing");
            var records = Enumerate(FileRecord.Create(
                "C:\\Docs\\Report.txt",
                isDirectory: false,
                sizeBytes: 42,
                DateTimeOffset.UtcNow));

            await Assert.ThrowsAnyAsync<Exception>(() =>
                ElevatedIndexerRecordWriter.WriteFileAsync(records, outputPath, CancellationToken.None));

            Assert.Equal("existing", await File.ReadAllTextAsync(outputPath));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task WriteAsyncFlushesCompletedBatchBeforeEnumerationFinishes()
    {
        var batchConsumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new FlushTrackingWriter();

        var writeTask = ElevatedIndexerRecordWriter.WriteAsync(
            EnumerateBatch(batchConsumed, allowCompletion),
            writer,
            CancellationToken.None);

        await batchConsumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(writer.FlushCount > 0);
        Assert.False(writeTask.IsCompleted);

        allowCompletion.TrySetResult();
        await writeTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async IAsyncEnumerable<FileRecord> Enumerate(params FileRecord[] records)
    {
        foreach (var record in records)
        {
            await Task.Yield();
            yield return record;
        }
    }

    private static async IAsyncEnumerable<FileRecord> EnumerateBatch(
        TaskCompletionSource batchConsumed,
        TaskCompletionSource allowCompletion)
    {
        for (var index = 0; index < ElevatedIndexerRecordWriter.StreamingFlushRecordCount; index++)
        {
            yield return FileRecord.Create(
                $"C:\\Docs\\{index}.txt",
                isDirectory: false,
                sizeBytes: index,
                DateTimeOffset.UnixEpoch);
        }

        batchConsumed.TrySetResult();
        await allowCompletion.Task;
    }

    private sealed class FlushTrackingWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public int FlushCount { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FlushCount++;
            return Task.CompletedTask;
        }
    }
}

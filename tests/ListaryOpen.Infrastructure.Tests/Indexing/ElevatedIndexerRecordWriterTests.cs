using ListaryOpen.Core.Indexing;
using ListaryOpen.Indexer.Elevated;

namespace ListaryOpen.Infrastructure.Tests.Indexing;

public sealed class ElevatedIndexerRecordWriterTests
{
    [Fact]
    public async Task WriteFileAsyncWritesWebJsonLines()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "listary-open-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDirectory);
        var outputPath = Path.Combine(Path.GetTempPath(), "listary-open-indexer-" + Guid.NewGuid() + ".jsonl");

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
                "{\"fullPath\":\"C:\\\\Docs\\\\Report.txt\",\"isDirectory\":false,\"sizeBytes\":42,\"lastWriteTime\":\"2026-07-04T01:02:03+00:00\"}",
                lines[0]);
            Assert.Equal(
                "{\"fullPath\":\"C:\\\\Docs\\\\Archive\",\"isDirectory\":true,\"sizeBytes\":0,\"lastWriteTime\":\"2026-07-04T01:03:00+00:00\"}",
                lines[1]);
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteFileAsyncDoesNotOverwriteExistingFile()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), "listary-open-indexer-" + Guid.NewGuid() + ".jsonl");
        await File.WriteAllTextAsync(outputPath, "existing");

        try
        {
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

    private static async IAsyncEnumerable<FileRecord> Enumerate(params FileRecord[] records)
    {
        foreach (var record in records)
        {
            await Task.Yield();
            yield return record;
        }
    }
}

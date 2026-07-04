using System.Text.Json;
using ListaryOpen.Core.Indexing;

namespace ListaryOpen.Indexer.Elevated;

internal static class ElevatedIndexerRecordWriter
{
    public static async Task WriteAsync(
        IAsyncEnumerable<FileRecord> records,
        TextWriter writer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(writer);

        await foreach (var record in records.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var line = JsonSerializer.Serialize(
                new
                {
                    record.FullPath,
                    record.IsDirectory,
                    record.SizeBytes,
                    record.LastWriteTime
                },
                JsonOptions.Default);

            await writer.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    public static async Task WriteFileAsync(
        IAsyncEnumerable<FileRecord> records,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        await using var stream = ElevatedIndexerOutputPathValidator.CreateNewFile(outputPath, ".jsonl");
        await using var writer = new StreamWriter(stream);
        await WriteAsync(records, writer, cancellationToken).ConfigureAwait(false);
    }
}

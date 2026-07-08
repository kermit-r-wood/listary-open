using System.Text.Json;
using ListaryOpen.Core.Indexing;
using ListaryOpen.Infrastructure.Indexing.Ntfs;

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

    public static async Task WriteJournalStateFileAsync(
        UsnJournalState state,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        await using var stream = ElevatedIndexerOutputPathValidator.CreateNewFile(outputPath, ".jsonl");
        await using var writer = new StreamWriter(stream);
        var line = JsonSerializer.Serialize(
            new
            {
                state.UsnJournalId,
                state.LowestValidUsn,
                state.NextUsn
            },
            JsonOptions.Default);

        await writer.WriteLineAsync(line).ConfigureAwait(false);
    }

    public static async Task WriteJournalChangesFileAsync(
        IAsyncEnumerable<UsnJournalChange> changes,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        await using var stream = ElevatedIndexerOutputPathValidator.CreateNewFile(outputPath, ".jsonl");
        await using var writer = new StreamWriter(stream);
        await foreach (var change in changes.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var line = JsonSerializer.Serialize(CreateChangeDto(change), JsonOptions.Default);
            await writer.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    private static object CreateChangeDto(UsnJournalChange change)
    {
        return change.Kind switch
        {
            UsnJournalChangeKind.Upsert => new
            {
                kind = "upsert",
                change.Record!.FullPath,
                change.Record.IsDirectory,
                change.Record.SizeBytes,
                change.Record.LastWriteTime
            },
            UsnJournalChangeKind.Delete => new
            {
                kind = "delete",
                change.FullPath
            },
            UsnJournalChangeKind.FileRename => new
            {
                kind = "fileRename",
                change.OldFullPath,
                change.Record!.FullPath,
                change.Record.IsDirectory,
                change.Record.SizeBytes,
                change.Record.LastWriteTime
            },
            UsnJournalChangeKind.DirectoryRenameOrMove => new
            {
                kind = "directoryRenameOrMove"
            },
            _ => throw new NotSupportedException($"Unsupported journal change kind: {change.Kind}.")
        };
    }
}
